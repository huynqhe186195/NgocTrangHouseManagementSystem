using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.Invoices;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Staged, immutable per-category account of a real independently reviewed
/// monthly invoice receipt. The approved receipt is a bank-evidence attestation,
/// not a gateway confirmation. These records are NOT debt-offset permissions.
/// A complete group (all lines for one receipt review) is required before any
/// line balance can be used for a deposit preview.
/// </summary>
public sealed class OfficialMonthlyReceiptLineAllocationRecord : Entity<Guid>
{
    public Guid ReceivablePostingId { get; private set; }
    public Guid ReceiptReviewId { get; private set; }
    public Guid ReceiptAllocationId { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid ContractId { get; private set; }
    public ContractDepositOffsetCategory Category { get; private set; }
    public decimal Amount { get; private set; }
    public Guid AllocatedByUserId { get; private set; }
    public DateTime AllocatedAtUtc { get; private set; }
    public string SignedAllocationReference { get; private set; } = string.Empty;
    public string InvoiceEvidenceSha256 { get; private set; } = string.Empty;

    public bool CanPostDebtOffset => false;
    public bool CanRefund => false;

    private OfficialMonthlyReceiptLineAllocationRecord() { }
    private OfficialMonthlyReceiptLineAllocationRecord(Guid id) : base(id) { }

    public static OfficialMonthlyReceiptLineAllocationRecord Capture(
        Guid id, OfficialInvoiceReceivablePostingRecord posting,
        OfficialInvoiceReceiptEvidence receipt,
        OfficialInvoiceReceiptReviewRecord bankReview,
        ContractDepositOffsetCategory category, decimal amount,
        Guid allocationReviewer, DateTime allocatedAtUtc,
        string signedAllocationReference)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(bankReview);
        bankReview.Verify(posting, receipt);
        if (id == Guid.Empty || posting.Kind != OfficialInvoiceKind.Monthly ||
            !bankReview.Accepted || !CategoryAllowed(category) ||
            amount <= 0m || amount != decimal.Truncate(amount) ||
            amount > bankReview.Amount ||
            allocationReviewer == Guid.Empty ||
            allocationReviewer == bankReview.ReviewedByUserId ||
            allocationReviewer == receipt.RecordedByUserId ||
            allocatedAtUtc.Kind != DateTimeKind.Utc ||
            allocatedAtUtc < bankReview.ReviewedAtUtc ||
            !Reference(signedAllocationReference))
            throw new InvalidOperationException(
                "Independent, signed and bank-reviewed monthly receipt line allocation required.");

        return new OfficialMonthlyReceiptLineAllocationRecord(id)
        {
            ReceivablePostingId = posting.Id,
            ReceiptReviewId = bankReview.Id,
            ReceiptAllocationId = receipt.AllocationId,
            PaymentId = receipt.PaymentId,
            ContractId = posting.ContractId,
            Category = category,
            Amount = amount,
            AllocatedByUserId = allocationReviewer,
            AllocatedAtUtc = allocatedAtUtc.AddTicks(-(allocatedAtUtc.Ticks % 10L)),
            SignedAllocationReference = signedAllocationReference.Trim(),
            InvoiceEvidenceSha256 = posting.SourceEvidenceSha256
        };
    }

    public void VerifyAgainst(OfficialInvoiceReceivablePostingRecord posting,
        OfficialInvoiceReceiptEvidence receipt,
        OfficialInvoiceReceiptReviewRecord bankReview)
    {
        bankReview.Verify(posting, receipt);
        if (Id == Guid.Empty || !bankReview.Accepted ||
            ReceivablePostingId != posting.Id ||
            ReceiptReviewId != bankReview.Id ||
            ReceiptAllocationId != receipt.AllocationId ||
            PaymentId != receipt.PaymentId ||
            ContractId != posting.ContractId ||
            posting.Kind != OfficialInvoiceKind.Monthly ||
            !CategoryAllowed(Category) ||
            Amount <= 0m || Amount != decimal.Truncate(Amount) ||
            Amount > bankReview.Amount ||
            AllocatedByUserId == Guid.Empty ||
            AllocatedByUserId == bankReview.ReviewedByUserId ||
            AllocatedByUserId == receipt.RecordedByUserId ||
            AllocatedAtUtc.Kind != DateTimeKind.Utc ||
            AllocatedAtUtc < bankReview.ReviewedAtUtc ||
            !Reference(SignedAllocationReference) ||
            !StringComparer.Ordinal.Equals(
                InvoiceEvidenceSha256, posting.SourceEvidenceSha256))
            throw new InvalidDataException(
                "Line allocation no longer matches original reviewed receipt or invoice.");
    }

    private static bool CategoryAllowed(ContractDepositOffsetCategory category) =>
        category is ContractDepositOffsetCategory.UnpaidRent or
            ContractDepositOffsetCategory.Electricity or
            ContractDepositOffsetCategory.Water or
            ContractDepositOffsetCategory.ApprovedService;

    private static bool Reference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 12 and <= 500;
}

/// <summary>
/// Authoritative category totals may be proposed only when every captured
/// receipt has its independent ACCEPT review AND a complete signed split.
/// There are no default payment-priority assumptions. Unreviewed, rejected,
/// orphaned, duplicated, or historically inconsistent evidence denies ALL.
/// </summary>
public static class OfficialMonthlyReceiptLineAllocationEvaluator
{
    public static OfficialMonthlyReceiptLineAllocationSummary Assess(
        OfficialInvoiceReceivablePostingRecord posting,
        MonthlyInvoiceCandidateLedgerRecord source,
        DateOnly asOfDate,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence>? receipts,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord>? reviews,
        IReadOnlyCollection<OfficialMonthlyReceiptLineAllocationRecord>? lines)
    {
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(source);
        if (asOfDate == default || receipts is null || reviews is null || lines is null)
            return Denied("MonthlyReceiptLineHistoryNotLoaded");
        if (posting.Kind != OfficialInvoiceKind.Monthly ||
            posting.ContractId != source.ContractId ||
            posting.SourceEvidenceSha256 != source.FrozenEvidenceSha256)
            return Denied("MonthlyReceiptLineInvoiceMismatch");

        var archived = source.VerifyStoredEvidence();
        var paid = receipts.ToArray();
        var signedReviews = reviews.ToArray();
        var allocations = lines.ToArray();
        if (paid.Any(x => x is null) || signedReviews.Any(x => x is null) ||
            allocations.Any(x => x is null) ||
            paid.Select(x => x.AllocationId).Distinct().Count() != paid.Length ||
            paid.Select(x => x.PaymentId).Distinct().Count() != paid.Length ||
            signedReviews.Select(x => x.ReceiptAllocationId)
                .Distinct().Count() != signedReviews.Length ||
            allocations.Select(x => x.Id).Distinct().Count() != allocations.Length ||
            allocations.GroupBy(x => (x.ReceiptReviewId, x.Category))
                .Any(x => x.Count() != 1))
            return Denied("DuplicateOrMissingMonthlyReceiptLineEvidence");

        var byAllocationId = paid.ToDictionary(x => x.AllocationId);
        var receiptReviews = signedReviews.ToDictionary(x => x.ReceiptAllocationId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        DateOnly BusinessDay(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

        if (paid.Length != signedReviews.Length ||
            paid.Any(x => x.IssuedInvoiceId != posting.IssuedInvoiceId ||
                x.ContractId != posting.ContractId ||
                !receiptReviews.ContainsKey(x.AllocationId)))
            return Denied("UnreviewedOrCrossInvoicePayment");

        try
        {
            foreach (var review in signedReviews)
            {
                if (!byAllocationId.TryGetValue(review.ReceiptAllocationId, out var receipt))
                    return Denied("OrphanedMonthlyBankReview");
                review.Verify(posting, receipt);
                if (!review.Accepted ||
                    BusinessDay(review.ReviewedAtUtc) > asOfDate ||
                    BusinessDay(receipt.PaidAtUtc) > asOfDate)
                    return Denied("RejectedOrFutureBankReceipt");
            }

            foreach (var line in allocations)
            {
                if (!byAllocationId.TryGetValue(line.ReceiptAllocationId, out var receipt) ||
                    !receiptReviews.TryGetValue(line.ReceiptAllocationId, out var review))
                    return Denied("OrphanedMonthlyLineAllocation");
                line.VerifyAgainst(posting, receipt, review);
                if (BusinessDay(line.AllocatedAtUtc) > asOfDate)
                    return Denied("FutureMonthlyLineAllocation");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            InvalidDataException or ArgumentException)
        {
            return Denied("MonthlyReceiptLineEvidenceInconsistent");
        }

        foreach (var bankReview in signedReviews)
        {
            var amount = allocations
                .Where(x => x.ReceiptReviewId == bankReview.Id)
                .Sum(x => x.Amount);
            if (amount != bankReview.Amount)
                return Denied("IncompleteOrOverAllocatedMonthlyReceipt");
        }
        var rent = Sum(ContractDepositOffsetCategory.UnpaidRent);
        var electricity = Sum(ContractDepositOffsetCategory.Electricity);
        var water = Sum(ContractDepositOffsetCategory.Water);
        var service = Sum(ContractDepositOffsetCategory.ApprovedService);
        if (rent > archived.RentAmount ||
            electricity > archived.ElectricityAmount ||
            water > archived.WaterAmount ||
            service > archived.AuxiliaryAmount ||
            checked(rent + electricity + water + service) !=
                signedReviews.Sum(x => x.Amount))
            return Denied("MonthlyLineAmountExceedsFrozenInvoice");

        return new OfficialMonthlyReceiptLineAllocationSummary(
            rent, electricity, water, service, Array.Empty<string>());

        decimal Sum(ContractDepositOffsetCategory category) =>
            allocations.Where(x => x.Category == category).Sum(x => x.Amount);
    }

    private static OfficialMonthlyReceiptLineAllocationSummary Denied(
        string code) => new(null, null, null, null, new[] { code });
}

public sealed record OfficialMonthlyReceiptLineAllocationSummary(
    decimal? PaidRentVnd, decimal? PaidElectricityVnd,
    decimal? PaidWaterVnd, decimal? PaidServiceVnd,
    IReadOnlyList<string> Blockers)
{
    public bool CanClassifyRemainingDebt => Blockers.Count == 0;
    public bool CanPostDebtOffset => false;
    public bool CanRefund => false;
}
