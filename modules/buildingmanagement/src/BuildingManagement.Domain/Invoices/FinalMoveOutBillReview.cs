using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Immutable review outcome for the FINAL month. Combines full contracted
/// rent with an existing persisted utility DRAFT. It is NOT an issued bill,
/// debt entry or authority to release a deposit or end the contract.
/// </summary>
public sealed class FinalMoveOutBillReview
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public Guid RoomId { get; init; }
    public int? BillingYear { get; init; }
    public int? BillingMonth { get; init; }
    public decimal? FullMonthRent { get; internal set; }
    public Guid? UtilityDraftId { get; internal set; }
    public decimal? ElectricityAmount { get; internal set; }
    public decimal? WaterAmount { get; internal set; }
    public decimal? ProvisionalRentAndUtilities { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanIssue => false;
    public bool IsReceivablePosted => false;
}

/// <summary>
/// Pure validation of persisted draft evidence. Never treats a draft as
/// payable, nor extrapolates unapproved Internet/trash/parking fees.
/// </summary>
public static class FinalMoveOutBillReviewEvaluator
{
    public static FinalMoveOutBillReview Assess(
        MoveOutRequest request,
        Contract? contract,
        IReadOnlyCollection<Invoice> periodInvoices,
        IReadOnlyCollection<InvoiceItem> persistedLines,
        IReadOnlyCollection<InvoiceItemMeterSource> meterSources)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(periodInvoices);
        ArgumentNullException.ThrowIfNull(persistedLines);
        ArgumentNullException.ThrowIfNull(meterSources);

        var rent = FinalMonthRentPreview.Assess(request, contract);
        var result = new FinalMoveOutBillReview
        {
            MoveOutRequestId = request.Id,
            ContractId = request.ContractId,
            RoomId = request.RoomId,
            BillingYear = rent.BillingYear,
            BillingMonth = rent.BillingMonth,
            FullMonthRent = rent.FullMonthRent
        };
        result.Blockers.AddRange(rent.Blockers);

        if (request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue ||
            rent.FullMonthRent is null)
        {
            result.Blockers.Add("FinalUtilityDraftNotReviewable");
        }
        else if (periodInvoices.Count != 1)
        {
            result.Blockers.Add(periodInvoices.Count == 0
                ? "FinalUtilityDraftMissing"
                : "FinalUtilityDraftAmbiguous");
        }
        else
        {
            var invoice = periodInvoices.Single();
            var date = request.ApprovedMoveOutDate.Value;
            if (invoice.Status != InvoiceStatus.Draft ||
                invoice.ContractId != request.ContractId ||
                invoice.RoomId != request.RoomId ||
                invoice.BillingYear != date.Year ||
                invoice.BillingMonth != date.Month)
            {
                result.Blockers.Add("FinalUtilityDraftScopeMismatch");
            }
            else if (!ValidateTwoUtilityLines(invoice, persistedLines, meterSources))
            {
                result.Blockers.Add("FinalUtilityDraftEvidenceInvalid");
            }
            else
            {
                var electricity = persistedLines.Single(x =>
                    x.UtilityType == UtilityType.Electricity).ProvisionalAmount!.Value;
                var water = persistedLines.Single(x =>
                    x.UtilityType == UtilityType.Water).ProvisionalAmount!.Value;
                result.UtilityDraftId = invoice.Id;
                result.ElectricityAmount = electricity;
                result.WaterAmount = water;
                result.ProvisionalRentAndUtilities =
                    result.FullMonthRent.Value + electricity + water;
            }
        }

        // Monetary policy / issuing / debt / deposit are not implemented.
        // Never turn a provisional quote into a receivable or a transfer.
        result.Blockers.Add("FinalInvoiceNotIssued");
        result.Blockers.Add("OutstandingDebtNotReconciled");
        result.Blockers.Add("DepositSettlementNotVerified");
        return result;
    }

    private static bool ValidateTwoUtilityLines(
        Invoice invoice,
        IReadOnlyCollection<InvoiceItem> items,
        IReadOnlyCollection<InvoiceItemMeterSource> sources)
    {
        if (items.Count != 2 ||
            items.Select(x => x.UtilityType).Distinct().Count() != 2 ||
            !items.Any(x => x.UtilityType == UtilityType.Electricity) ||
            !items.Any(x => x.UtilityType == UtilityType.Water) ||
            sources.Count == 0 ||
            sources.Any(x =>
                x.Id == Guid.Empty ||
                x.InvoiceItemId == Guid.Empty ||
                x.SourceId == Guid.Empty ||
                x.UtilityMeterId == Guid.Empty ||
                !Enum.IsDefined(x.SourceType) ||
                x.PreviousReading < 0 ||
                x.CurrentReading < x.PreviousReading) ||
            sources.Select(x => (x.SourceType, x.SourceId)).Distinct().Count()
                != sources.Count)
            return false;

        foreach (var item in items)
        {
            if (item.InvoiceId != invoice.Id ||
                item.Id == Guid.Empty ||
                item.RecordedQuantity < 0 ||
                item.UtilityRateId.GetValueOrDefault() == Guid.Empty ||
                item.UnitPrice is not decimal price || price <= 0 ||
                item.ProvisionalAmount is not decimal amount || amount < 0 ||
                item.RateStatus != "SingleRateWholeMonth" ||
                item.CoverageIssuesJson != "[]" ||
                item.RecordedQuantity * price != amount)
                return false;

            var segments = sources.Where(x => x.InvoiceItemId == item.Id).ToArray();
            if (segments.Length == 0 ||
                segments.Sum(x => x.Quantity) != item.RecordedQuantity)
                return false;
        }

        if (sources.Any(x => items.All(i => i.Id != x.InvoiceItemId)))
            return false;

        return invoice.ProvisionalUtilityTotal.HasValue &&
               invoice.ProvisionalUtilityTotal.Value ==
                   items.Sum(x => x.ProvisionalAmount!.Value);
    }
}
