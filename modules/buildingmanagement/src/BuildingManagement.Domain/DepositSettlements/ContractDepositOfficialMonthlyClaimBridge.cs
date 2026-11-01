using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Invoices;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Converts a genuinely posted, independently issued FULLY UNPAID monthly
/// invoice into OI-04 preview claims while checking the immutable archived
/// price breakdown against the OFFICIAL posting and as-of debt evaluator.
/// No owner approval, debt offset, bank transaction, or refund is performed.
/// A partially paid invoice cannot be split into debt categories until a
/// verified payment-to-line allocation policy/journal exists.
/// The application caller must load ALL relevant receipts, reviews, proposed
/// adjustments and decisions from the database (including soft-deleted
/// payment evidence); omitting history is an authorization/data-integrity
/// failure, never an alternative payment policy.
/// </summary>
public static class ContractDepositOfficialMonthlyClaimBridge
{
    public static ContractDepositOfficialMonthlyClaims Assess(
        OfficialInvoiceReceivablePostingRecord? posting,
        MonthlyIssuedInvoiceRecord? issued,
        MonthlyInvoiceCandidateLedgerRecord? source,
        MonthlyInvoiceIndependentReviewRecord? review,
        DateOnly asOfDate,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence>? receipts,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord>? receiptReviews,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord>? adjustmentProposals,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord>? adjustmentDecisions,
        IReadOnlyCollection<OfficialMonthlyReceiptLineAllocationRecord>? lineAllocations = null)
    {
        var blockers = new List<string>();
        if (posting is null || issued is null || source is null || review is null)
            return Denied("OfficialMonthlyEvidenceMissing");
        if (asOfDate == default || receipts is null || receiptReviews is null ||
            adjustmentProposals is null || adjustmentDecisions is null)
            return Denied("OfficialMonthlyHistoryNotLoaded");

        // Validate the original invoice, independent review, archived tariff
        // and the separately persisted posted receivable before using any
        // category amount. This is stronger than trusting a caller's balance.
        posting.VerifyMonthly(issued, source, review);
        var archived = source.VerifyStoredEvidence();
        if (posting.Kind != OfficialInvoiceKind.Monthly ||
            posting.IssuedInvoiceId != issued.Id ||
            posting.ContractId != source.ContractId ||
            posting.BuildingId != source.BuildingId ||
            posting.RoomId != source.RoomId)
            throw new InvalidDataException("Monthly official posting scope differs from source.");

        // As-of balance cannot include an invoice that was not yet POSTED
        // in Vietnam business time. A future posting is not historical debt.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var postedOn = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(posting.PostedAtUtc, zone));
        if (postedOn > asOfDate)
            blockers.Add("OfficialPostingIsAfterEvaluationDate");

        // Existing callers without independent signed line evidence remain
        // fail-closed for any payment. An optional fully validated new
        // journal may explicitly classify a verified partial payment.
        OfficialMonthlyReceiptLineAllocationSummary? paidLines = null;
        if (receipts.Count != 0 || receiptReviews.Count != 0 ||
            lineAllocations is { Count: > 0 })
        {
            if (lineAllocations is null)
                blockers.Add("PartialOrUnresolvedPaymentsNeedLineAllocation");
            else
            {
                paidLines = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
                    posting, source, asOfDate, receipts, receiptReviews,
                    lineAllocations);
                if (!paidLines.CanClassifyRemainingDebt)
                    blockers.AddRange(paidLines.Blockers);
            }
        }

        // Adjustments, even rejected/in-flight, require an official verified
        // correction chain, not an invented per-category credit assignment.
        if (adjustmentProposals.Count != 0 || adjustmentDecisions.Count != 0)
            blockers.Add("AdjustedInvoiceNeedsLineAllocation");

        var balance = OfficialInvoiceReceivableBalanceEvaluator.Assess(
            posting, asOfDate, 0m, receipts, receiptReviews);
        if (!balance.IsOfficialDebtPosted ||
            balance.OriginalAmount != source.TotalAmount ||
            balance.AdjustedAmount != source.TotalAmount ||
            (paidLines is null
                ? balance.IndependentlyReviewedManualPayments != 0m
                : balance.IndependentlyReviewedManualPayments !=
                    paidLines.PaidRentVnd + paidLines.PaidElectricityVnd +
                    paidLines.PaidWaterVnd + paidLines.PaidServiceVnd) ||
            (paidLines is null && balance.OutstandingAmount != source.TotalAmount) ||
            balance.PendingOrRejectedPaymentCount != 0)
            blockers.Add("OfficialOutstandingDoesNotMatchFrozenLines");

        if (blockers.Count != 0)
            return new(Array.Empty<ContractDepositOffsetClaim>(),
                blockers.Distinct(StringComparer.Ordinal).ToArray());

        var claims = new List<ContractDepositOffsetClaim>(4);
        Add(claims, posting, ContractDepositOffsetCategory.UnpaidRent,
            archived.RentAmount - (paidLines?.PaidRentVnd ?? 0m));
        Add(claims, posting, ContractDepositOffsetCategory.Electricity,
            archived.ElectricityAmount - (paidLines?.PaidElectricityVnd ?? 0m));
        Add(claims, posting, ContractDepositOffsetCategory.Water,
            archived.WaterAmount - (paidLines?.PaidWaterVnd ?? 0m));
        Add(claims, posting, ContractDepositOffsetCategory.ApprovedService,
            archived.AuxiliaryAmount - (paidLines?.PaidServiceVnd ?? 0m));

        if (claims.Sum(x => x.OutstandingVnd) != balance.OutstandingAmount)
            throw new InvalidDataException("Monthly debt category totals do not match official outstanding.");

        return new(claims.AsReadOnly(), Array.Empty<string>());
    }

    private static void Add(
        List<ContractDepositOffsetClaim> claims,
        OfficialInvoiceReceivablePostingRecord posting,
        ContractDepositOffsetCategory category, decimal amount)
    {
        if (amount == 0m) return;
        if (amount < 0m || decimal.Truncate(amount) != amount)
            throw new InvalidDataException("Invalid archived monthly charge.");
        var id = ClaimId(posting.Id, category);
        claims.Add(new ContractDepositOffsetClaim(
            id, category, amount,
            IndependentlyVerified: true, IsDisputed: false,
            SignedEvidenceReference: posting.SourceEvidenceSha256));
    }

    private static Guid ClaimId(Guid posting,
        ContractDepositOffsetCategory category)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            "OI-04/OFFICIAL-MONTHLY/" + posting.ToString("D") + "/" +
            ((int)category).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static ContractDepositOfficialMonthlyClaims Denied(string code)
        => new(Array.Empty<ContractDepositOffsetClaim>(), new[] { code });
}

public sealed record ContractDepositOfficialMonthlyClaims(
    IReadOnlyList<ContractDepositOffsetClaim> Claims,
    IReadOnlyList<string> Blockers)
{
    public bool CanPreview => Blockers.Count == 0;
    public bool CanPostOffset => false;
    public bool CanRefund => false;
}
