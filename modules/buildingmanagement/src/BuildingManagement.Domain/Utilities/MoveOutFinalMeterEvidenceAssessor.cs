using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Utilities;

/// <summary>
/// Read-only evidence of a final physical counter reading on the approved
/// move-out date. It never approves handover, issues a final invoice, or
/// determines who is liable for historical consumption.
/// </summary>
public sealed record MoveOutFinalMeterEvidence(
    UtilityType UtilityType,
    Guid? UtilityMeterId,
    Guid? MeterReadingId,
    decimal? FinalReading,
    string Status)
{
    public bool HasValidReadingEvidence => Status == "ReadingEvidencePresent";
}

public static class MoveOutFinalMeterEvidenceAssessor
{
    public static MoveOutFinalMeterEvidence Assess(
        Guid roomId,
        UtilityType type,
        DateOnly moveOutDate,
        IEnumerable<UtilityMeter> meters,
        IEnumerable<MeterReading> readings,
        IEnumerable<MeterReplacement> replacements,
        IReadOnlyCollection<string> coverageIssues)
    {
        if (roomId == Guid.Empty)
            throw new ArgumentException("Room is required.", nameof(roomId));
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(coverageIssues);

        var date = moveOutDate.ToDateTime(TimeOnly.MinValue);
        var roomMeters = meters
            .Where(x => x.RoomId == roomId && x.UtilityType == type)
            .ToList();
        var roomReplacements = replacements
            .Where(x => x.RoomId == roomId && x.UtilityType == type)
            .ToList();

        if (roomMeters.Count == 0)
            return new MoveOutFinalMeterEvidence(type, null, null, null,
                "NoMeterForRoomAndUtility");

        // Current IsActive cannot reconstruct historical meter state. An
        // outgoing handover at/before the move-out date closes the old meter;
        // later replacements do not change which meter was used on that date.
        var candidates = roomMeters
            .Where(x => x.InstalledDate.Date <= date &&
                        !roomReplacements.Any(r =>
                            r.OldMeterId == x.Id && r.ReplacementDate.Date <= date))
            .ToList();

        if (candidates.Count != 1)
            return new MoveOutFinalMeterEvidence(type, null, null, null,
                candidates.Count == 0 ? "NoMeterAtMoveOutDate" :
                "AmbiguousMeterAtMoveOutDate");

        var meter = candidates[0];

        // Manual deactivation has no effective date in the current model.
        // Without a recorded replacement event we cannot reconstruct if
        // this meter was still operating at the move-out date.
        if (!meter.IsActive &&
            !roomReplacements.Any(r => r.OldMeterId == meter.Id))
            return new MoveOutFinalMeterEvidence(type, meter.Id, null, null,
                "MeterLifecycleRequiresReview");

        var matching = readings.Where(x =>
            x.UtilityMeterId == meter.Id &&
            x.BillingYear == moveOutDate.Year &&
            x.BillingMonth == moveOutDate.Month &&
            x.ReadingDate.Date == date &&
            !x.IsDeleted).ToList();

        if (matching.Count != 1)
            return new MoveOutFinalMeterEvidence(type, meter.Id, null, null,
                matching.Count == 0 ? "FinalReadingMissingOrWrongDate" :
                "AmbiguousFinalReadings");

        var reading = matching[0];
        if (coverageIssues.Count != 0)
            return new MoveOutFinalMeterEvidence(type, meter.Id, reading.Id,
                reading.CurrentReading, "UsageCoverageRequiresReview");

        return new MoveOutFinalMeterEvidence(
            type, meter.Id, reading.Id, reading.CurrentReading,
            "ReadingEvidencePresent");
    }
}
