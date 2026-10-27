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
/// Replacement-closing events remain a separate proof workflow and are
/// conservatively marked unverified until a typed source adapter exists.
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
        IReadOnlyCollection<UtilityRate> rates)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(rates);

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
            readings.Select(x => x.Id).Distinct().Count() != readings.Count ||
            meters.Select(x => x.Id).Distinct().Count() != meters.Count ||
            rates.Select(x => x.Id).Distinct().Count() != rates.Count)
        {
            result.Blockers.Add("SourceEvidenceIdentifiersInvalid");
            return Stop(result);
        }

        var readingById = readings.ToDictionary(x => x.Id);
        var meterById = meters.ToDictionary(x => x.Id);
        var rateById = rates.ToDictionary(x => x.Id);
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

                if (segment.SourceType != MeterUsageSourceType.MonthlyReading)
                {
                    // Replacement-closing records are historical evidence
                    // but NOT interchangeable with MeterReading. Until
                    // explicitly loaded and verified, do not report green.
                    result.Blockers.Add(
                        "ReplacementClosingSourceRequiresIndependentAudit");
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
