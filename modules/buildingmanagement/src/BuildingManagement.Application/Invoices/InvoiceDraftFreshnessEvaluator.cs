using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Conservative, pure comparison of a persisted utility-only draft with a
/// freshly reconstructed invoice candidate. Any missing, duplicate, changed,
/// or unpriced source means the draft is stale. Never mutates the snapshot.
///
/// NOT an authorization check or an issuance API. Any future issuance must
/// perform this check INSIDE a protected transaction together with the
/// final state change; a detached earlier true result is not a permit.
/// </summary>
public static class InvoiceDraftFreshnessEvaluator
{
    public static bool MatchesCurrentCandidate(
        InvoiceDraftDto saved, InvoiceDraftCandidateDto current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);

        if (saved.Id == Guid.Empty || saved.ContractId == Guid.Empty ||
            saved.RoomId == Guid.Empty || saved.Status != InvoiceStatus.Draft ||
            saved.ContractId != current.ContractId ||
            saved.RoomId != current.RoomId ||
            saved.BillingYear != current.BillingYear ||
            saved.BillingMonth != current.BillingMonth ||
            saved.ProvisionalUtilityTotal is null ||
            current.ProvisionalTotal is null ||
            saved.ProvisionalUtilityTotal != current.ProvisionalTotal ||
            current.Blockers is null || current.Blockers.Count != 0 ||
            saved.Items is null || current.UtilityLines is null ||
            saved.Items.Count != 2 || current.UtilityLines.Count != 2)
            return false;

        if (saved.Items.Any(x => x is null) ||
            current.UtilityLines.Any(x => x is null) ||
            saved.Items.Select(x => x.UtilityType).Distinct().Count() != 2 ||
            current.UtilityLines.Select(x => x.UtilityType).Distinct().Count() != 2)
            return false;

        var currentByType = current.UtilityLines.ToDictionary(x => x.UtilityType);
        var savedSources = new HashSet<(string SourceType, Guid SourceId)>();
        var freshSources = new HashSet<(string SourceType, Guid SourceId)>();
        decimal savedTotal = 0;
        decimal freshTotal = 0;

        foreach (var item in saved.Items)
        {
            if (!currentByType.TryGetValue(item.UtilityType, out var candidate) ||
                !Enum.IsDefined(typeof(UtilityType), item.UtilityType) ||
                item.UtilityRateId.GetValueOrDefault() == Guid.Empty ||
                item.RateStatus != "SingleRateWholeMonth" ||
                item.UtilityRateId != candidate.UtilityRateId ||
                item.RateStatus != candidate.RateStatus ||
                item.Quantity != candidate.RecordedQuantity ||
                item.UnitPrice is null || item.UnitPrice <= 0 ||
                item.UnitPrice != candidate.UnitPrice ||
                item.ProvisionalAmount is null ||
                item.ProvisionalAmount != candidate.ProvisionalAmount ||
                item.ProvisionalAmount != item.Quantity * item.UnitPrice ||
                item.CoverageIssues is null || item.CoverageIssues.Count != 0 ||
                candidate.CoverageIssues is null || candidate.CoverageIssues.Count != 0 ||
                item.Sources is null || candidate.MeterSegments is null ||
                item.Sources.Count == 0 ||
                item.Sources.Count != candidate.MeterSegments.Count)
                return false;

            savedTotal += item.ProvisionalAmount.Value;
            freshTotal += candidate.ProvisionalAmount.Value;

            var candidateByKey = new Dictionary<(string SourceType, Guid SourceId), MeterUsageSegmentDto>();
            foreach (var segment in candidate.MeterSegments)
            {
                if (segment is null || segment.SourceId == Guid.Empty ||
                    segment.UtilityMeterId == Guid.Empty ||
                    !Enum.TryParse<MeterUsageSourceType>(segment.SourceType, out var sourceType) ||
                    !Enum.IsDefined(typeof(MeterUsageSourceType), sourceType) ||
                    segment.SourceType != sourceType.ToString() ||
                    !candidateByKey.TryAdd((segment.SourceType, segment.SourceId), segment) ||
                    !freshSources.Add((segment.SourceType, segment.SourceId)))
                    return false;
            }

            decimal savedQuantity = 0;
            decimal freshQuantity = 0;
            foreach (var source in item.Sources)
            {
                if (source is null || source.SourceId == Guid.Empty ||
                    source.UtilityMeterId == Guid.Empty ||
                    !candidateByKey.TryGetValue((source.SourceType, source.SourceId),
                        out var currentSource) ||
                    !savedSources.Add((source.SourceType, source.SourceId)) ||
                    source.UtilityMeterId != currentSource.UtilityMeterId ||
                    source.RecordedDate != currentSource.RecordedDate ||
                    source.PreviousReading != currentSource.PreviousReading ||
                    source.CurrentReading != currentSource.CurrentReading ||
                    source.Quantity != currentSource.Usage ||
                    source.Quantity != source.CurrentReading - source.PreviousReading ||
                    source.Quantity < 0)
                    return false;

                savedQuantity += source.Quantity;
            }

            foreach (var segment in candidate.MeterSegments)
            {
                if (segment.CurrentReading < segment.PreviousReading ||
                    segment.Usage != segment.CurrentReading - segment.PreviousReading)
                    return false;
                freshQuantity += segment.Usage;
            }

            if (savedQuantity != item.Quantity || freshQuantity != candidate.RecordedQuantity)
                return false;
        }

        return savedTotal == saved.ProvisionalUtilityTotal.Value &&
               freshTotal == current.ProvisionalTotal.Value &&
               savedSources.SetEquals(freshSources);
    }
}
