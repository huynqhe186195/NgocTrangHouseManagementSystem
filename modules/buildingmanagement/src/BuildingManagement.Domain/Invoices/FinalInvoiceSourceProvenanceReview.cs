using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only verification of the ORIGINAL MeterReading, UtilityMeter and
/// UtilityRate behind archived final-invoice draft evidence. This does not
/// replace the archived monetary snapshot or reprice historical invoices.
/// Replacement-closing events are checked against the actual archived
/// handover and the old/new physical meters, never treated as readings.
/// </summary>
public sealed class FinalInvoiceSourceProvenanceReview
{
    public Guid StagedEvidenceId { get; init; }
    public bool MatchesPersistedSources { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanIssue => false;
    public bool CanPostReceivable => false;
}

public static class FinalInvoiceSourceProvenanceEvaluator
{
    public static FinalInvoiceSourceProvenanceReview Assess(
        FinalInvoiceIssueEvidenceRecord archive,
        Guid resolvedBuildingId,
        IReadOnlyCollection<MeterReading> readings,
        IReadOnlyCollection<UtilityMeter> meters,
        IReadOnlyCollection<UtilityRate> rates,
        IReadOnlyCollection<MeterReplacement>? replacements = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(rates);
        replacements ??= Array.Empty<MeterReplacement>();

        var result = new FinalInvoiceSourceProvenanceReview
        {
            StagedEvidenceId = archive.Id
        };

        FinalInvoiceUtilityEvidence[] utilities;
        try
        {
            archive.VerifyStoredEvidence();
            using var json = JsonDocument.Parse(archive.EvidenceJson);
            utilities = JsonSerializer.Deserialize<FinalInvoiceUtilityEvidence[]>(
                json.RootElement.GetProperty("Utilities").GetRawText())
                ?? throw new JsonException("Missing utility evidence.");
        }
        catch (Exception e) when (e is InvalidDataException or JsonException
            or ArgumentException or InvalidOperationException)
        {
            result.Blockers.Add("ArchivedSourceEvidenceInvalid");
            return Stop(result);
        }

        if (resolvedBuildingId == Guid.Empty)
            result.Blockers.Add("SourceBuildingScopeUnavailable");

        if (readings.Any(x => x is null || x.Id == Guid.Empty) ||
            meters.Any(x => x is null || x.Id == Guid.Empty) ||
            rates.Any(x => x is null || x.Id == Guid.Empty) ||
            replacements.Any(x => x is null || x.Id == Guid.Empty) ||
            readings.Select(x => x.Id).Distinct().Count() != readings.Count ||
            meters.Select(x => x.Id).Distinct().Count() != meters.Count ||
            rates.Select(x => x.Id).Distinct().Count() != rates.Count ||
            replacements.Select(x => x.Id).Distinct().Count() != replacements.Count)
        {
            result.Blockers.Add("SourceEvidenceIdentifiersInvalid");
            return Stop(result);
        }

        var readingById = readings.ToDictionary(x => x.Id);
        var meterById = meters.ToDictionary(x => x.Id);
        var rateById = rates.ToDictionary(x => x.Id);
        var replacementById = replacements.ToDictionary(x => x.Id);
        var firstDay = new DateTime(archive.BillingYear, archive.BillingMonth, 1);
        var lastDay = new DateTime(
            archive.BillingYear, archive.BillingMonth,
            DateTime.DaysInMonth(archive.BillingYear, archive.BillingMonth));

        foreach (var utility in utilities)
        {
            if (!rateById.TryGetValue(utility.UtilityRateId, out var rate))
                result.Blockers.Add("OriginalUtilityRateMissing");
            else
            {
                if (rate.IsDeleted)
                    result.Blockers.Add("OriginalUtilityRateSoftDeleted");
                if (rate.BuildingId != resolvedBuildingId ||
                    rate.UtilityType != utility.UtilityType ||
                    rate.UnitPrice != utility.UnitPrice ||
                    rate.EffectiveFrom.Date > firstDay ||
                    (rate.EffectiveTo.HasValue &&
                     rate.EffectiveTo.Value.Date < lastDay))
                    result.Blockers.Add("OriginalUtilityRateDiffersFromSnapshot");
            }

            foreach (var segment in utility.Sources)
            {
                if (!meterById.TryGetValue(segment.UtilityMeterId, out var meter))
                {
                    result.Blockers.Add("OriginalUtilityMeterMissing");
                    continue;
                }
                if (meter.IsDeleted)
                    result.Blockers.Add("OriginalUtilityMeterSoftDeleted");
                // A replaced/inactive meter can still support historic
                // usage; only room/type are invariant, not IsActive.
                if (meter.RoomId != archive.RoomId ||
                    meter.UtilityType != utility.UtilityType)
                    result.Blockers.Add("OriginalMeterResourceScopeMismatch");

                if (segment.SourceType == MeterUsageSourceType.ReplacementClosing)
                {
                    // A replacement snapshot is NEVER a monthly reading:
                    // the archived meter is the OLD meter, and SourceId
                    // refers to a separate immutable MeterReplacement.
                    if (!replacementById.TryGetValue(segment.SourceId,
                        out var replacement))
                    {
                        result.Blockers.Add("OriginalMeterReplacementMissing");
                        continue;
                    }

                    if (replacement.OldMeterId != segment.UtilityMeterId ||
                        replacement.NewMeterId == replacement.OldMeterId ||
                        replacement.RoomId != archive.RoomId ||
                        replacement.UtilityType != utility.UtilityType ||
                        replacement.ReplacementDate.Date != segment.RecordedDate.Date ||
                        segment.RecordedDate.TimeOfDay != TimeSpan.Zero ||
                        replacement.OldPreviousReading != segment.PreviousReading ||
                        replacement.OldFinalReading != segment.CurrentReading ||
                        replacement.OldFinalReading < replacement.OldPreviousReading)
                        result.Blockers.Add(
                            "OriginalMeterReplacementClosingDiffersFromSnapshot");

                    if (!meterById.TryGetValue(replacement.NewMeterId,
                        out var newMeter))
                    {
                        result.Blockers.Add("ReplacementNewMeterMissing");
                        continue;
                    }

                    if (newMeter.IsDeleted)
                        result.Blockers.Add("ReplacementNewMeterSoftDeleted");
                    if (newMeter.RoomId != archive.RoomId ||
                        newMeter.UtilityType != utility.UtilityType ||
                        newMeter.InitialReading != replacement.NewInitialReading ||
                        newMeter.InstalledDate.Date != replacement.ReplacementDate.Date)
                        result.Blockers.Add("ReplacementNewMeterOpeningMismatch");
                    continue;
                }

                if (segment.SourceType != MeterUsageSourceType.MonthlyReading)
                {
                    result.Blockers.Add("UnsupportedMeterUsageSourceType");
                    continue;
                }

                if (!readingById.TryGetValue(segment.SourceId, out var reading))
                {
                    result.Blockers.Add("OriginalMeterReadingMissing");
                    continue;
                }
                if (reading.IsDeleted)
                    result.Blockers.Add("OriginalMeterReadingSoftDeleted");
                if (reading.UtilityMeterId != segment.UtilityMeterId ||
                    reading.BillingYear != archive.BillingYear ||
                    reading.BillingMonth != archive.BillingMonth ||
                    reading.ReadingDate != segment.RecordedDate ||
                    reading.PreviousReading != segment.PreviousReading ||
                    reading.CurrentReading != segment.CurrentReading)
                    result.Blockers.Add("OriginalMeterReadingDiffersFromSnapshot");
            }
        }

        result.MatchesPersistedSources = result.Blockers.Count == 0;
        return Stop(result);
    }

    private static FinalInvoiceSourceProvenanceReview Stop(
        FinalInvoiceSourceProvenanceReview result)
    {
        result.Blockers.Add("OriginalSourcesAuditIsNotIssuanceAuthorization");
        return result;
    }
}
