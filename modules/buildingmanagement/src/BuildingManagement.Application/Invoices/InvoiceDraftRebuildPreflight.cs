using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Outcomes of a PURE draft correction preflight. None of these values
/// changes invoice state, releases unique indexes, or permits issuance.
/// </summary>
public enum InvoiceDraftRebuildGateStatus
{
    ReadyForRevisionDesign = 0,
    AlreadyCurrent = 1,
    ReasonRequired = 2,
    SavedDraftInvalid = 3,
    DifferentBillingScope = 4,
    CurrentCandidateNotReady = 5
}

public sealed record InvoiceDraftRebuildGateResult(
    InvoiceDraftRebuildGateStatus Status,
    decimal? ProposedUtilityTotal = null,
    string? NormalizedReason = null)
{
    public bool CanPrepareRevision =>
        Status == InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign;
}

/// <summary>
/// Phase 2G.3A.7: pure preflight for a possible future *versioned* rebuild.
/// It never mutates a persisted draft or issues a financial charge.
///
/// IMPORTANT: ReadyForRevisionDesign is NOT a durable permission token.
/// It is only a point-in-time assessment. Any eventual RebuildDraft
/// transaction must repeat validation after authorization and locking,
/// atomically persist old revision evidence and the new snapshot,
/// and handle competing source edits and concurrent rebuild requests.
/// </summary>
public static class InvoiceDraftRebuildPreflight
{
    public static InvoiceDraftRebuildGateResult Assess(
        InvoiceDraftDto saved,
        InvoiceDraftCandidateDto current,
        string? reason)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);

        if (!IsValidSavedDraft(saved))
            return new(InvoiceDraftRebuildGateStatus.SavedDraftInvalid);

        if (saved.ContractId != current.ContractId ||
            saved.RoomId != current.RoomId ||
            saved.BillingYear != current.BillingYear ||
            saved.BillingMonth != current.BillingMonth)
            return new(InvoiceDraftRebuildGateStatus.DifferentBillingScope);

        if (InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current))
            return new(InvoiceDraftRebuildGateStatus.AlreadyCurrent);

        var cleanedReason = reason?.Trim();
        // Require a meaningful operator explanation, not a silent
        // automated rewrite of previously captured financial evidence.
        if (string.IsNullOrWhiteSpace(cleanedReason) ||
            cleanedReason.Length < 8 || cleanedReason.Length > 500)
            return new(InvoiceDraftRebuildGateStatus.ReasonRequired);

        var proposed = TryCapture(
            current.ContractId, current.RoomId,
            current.BillingYear, current.BillingMonth,
            current.UtilityLines?.Select(x =>
                x is null ? null : (LineData?)new LineData(
                    x.UtilityType, x.UtilityRateId, x.RateStatus,
                    x.RecordedQuantity, x.UnitPrice,
                    x.ProvisionalAmount, x.CoverageIssues,
                    x.MeterSegments?.Select(y =>
                        y is null ? null : (SourceData?)new SourceData(
                            y.SourceId, y.UtilityMeterId, y.SourceType,
                            y.RecordedDate, y.PreviousReading,
                            y.CurrentReading, y.Usage)).ToArray())).ToArray(),
            current.Blockers);

        if (proposed is null ||
            proposed.ProvisionalTotal is null ||
            proposed.ProvisionalTotal != current.ProvisionalTotal)
            return new(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady);

        return new(InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign,
            proposed.ProvisionalTotal, cleanedReason);
    }

    private static bool IsValidSavedDraft(InvoiceDraftDto saved)
    {
        if (saved.Id == Guid.Empty || saved.ContractId == Guid.Empty ||
            saved.RoomId == Guid.Empty || saved.Status != InvoiceStatus.Draft ||
            saved.Items is null || saved.Items.Count != 2 ||
            saved.Items.Any(x => x is null || x.Id == Guid.Empty ||
                                 x.Sources is null || x.Sources.Count == 0))
            return false;

        var snapshot = TryCapture(
            saved.ContractId, saved.RoomId,
            saved.BillingYear, saved.BillingMonth,
            saved.Items.Select(x => (LineData?)new LineData(
                x.UtilityType, x.UtilityRateId, x.RateStatus,
                x.Quantity, x.UnitPrice, x.ProvisionalAmount,
                x.CoverageIssues,
                x.Sources.Select(y => y is null ? null : (SourceData?)
                    new SourceData(y.SourceId, y.UtilityMeterId,
                        y.SourceType, y.RecordedDate, y.PreviousReading,
                        y.CurrentReading, y.Quantity)).ToArray())).ToArray(),
            Array.Empty<string>());

        return snapshot is not null &&
               snapshot.ProvisionalTotal is not null &&
               snapshot.ProvisionalTotal == saved.ProvisionalUtilityTotal;
    }

    private sealed record LineData(
        UtilityType Type, Guid? RateId, string RateStatus,
        decimal Quantity, decimal? UnitPrice, decimal? Amount,
        IReadOnlyCollection<string>? Issues, SourceData?[]? Sources);

    private sealed record SourceData(
        Guid SourceId, Guid MeterId, string SourceType,
        DateTime RecordedDate, decimal Previous,
        decimal Current, decimal Quantity);

    private static InvoiceDraftSnapshot? TryCapture(
        Guid contractId, Guid roomId, int year, int month,
        LineData?[]? lines, IReadOnlyCollection<string>? blockers)
    {
        if (contractId == Guid.Empty || roomId == Guid.Empty ||
            year is < 1 or > 9999 || month is < 1 or > 12 ||
            blockers is null || blockers.Count != 0 ||
            lines is null || lines.Length != 2 ||
            lines.Any(x => x is null) ||
            lines.Select(x => x!.Type).Distinct().Count() != 2 ||
            !lines.Select(x => x!.Type).ToHashSet().SetEquals(
                new[] { UtilityType.Electricity, UtilityType.Water }))
            return null;

        var snapshots = new List<InvoiceDraftUtilityLineSnapshot>();
        foreach (var line in lines)
        {
            if (line is null || line.RateId.GetValueOrDefault() == Guid.Empty ||
                line.RateStatus != "SingleRateWholeMonth" ||
                line.Amount is null || line.UnitPrice is null ||
                line.Issues is null || line.Issues.Count != 0 ||
                line.Sources is null || line.Sources.Length == 0)
                return null;

            var sourceSnapshots = new List<InvoiceDraftMeterSegmentSnapshot>();
            foreach (var source in line.Sources)
            {
                if (source is null || source.SourceId == Guid.Empty ||
                    source.MeterId == Guid.Empty ||
                    !Enum.TryParse<MeterUsageSourceType>(
                        source.SourceType, out var sourceType) ||
                    !Enum.IsDefined(typeof(MeterUsageSourceType), sourceType) ||
                    source.SourceType != sourceType.ToString() ||
                    source.Current < source.Previous ||
                    source.Quantity != source.Current - source.Previous ||
                    source.RecordedDate == default)
                    return null;

                sourceSnapshots.Add(new InvoiceDraftMeterSegmentSnapshot(
                    source.SourceId, source.MeterId, sourceType,
                    source.RecordedDate, source.Previous, source.Current));
            }

            try
            {
                snapshots.Add(new InvoiceDraftUtilityLineSnapshot(
                    line.Type, line.RateId, line.RateStatus,
                    line.Quantity, line.UnitPrice, line.Amount,
                    sourceSnapshots, line.Issues));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        try
        {
            return InvoiceDraftSnapshot.Capture(contractId, roomId,
                year, month, snapshots);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
