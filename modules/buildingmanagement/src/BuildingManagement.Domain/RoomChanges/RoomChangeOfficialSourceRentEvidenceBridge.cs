using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Phase 2H / D03 READ ONLY. Rebuilds the source RENT component from the
/// exact issued MONTHLY archive, real posted receivable, independently
/// reviewed manual-bank receipts, and COMPLETE signed per-line allocations.
/// No caller-supplied rent total, paid balance, invoice hash or claimed
/// "already corrected" amount can enter this projection.
///
/// Existing monthly adjustment decisions are invoice TOTAL deltas and
/// cannot safely be allocated to RENT without a separately approved
/// line-classified correction ledger. Fail CLOSED for any such history.
/// The caller must load COMPLETE history, including disputed/soft-deleted
/// receipts, from the authoritative server-scoped database. This domain
/// evaluator does not independently authenticate the history loader.
/// </summary>
public static class RoomChangeOfficialSourceRentEvidenceBridge
{
    public static RoomChangeOfficialSourceRentEvidenceResult Assess(
        RoomChangeDailyRentQuote? quote,
        OfficialInvoiceReceivablePostingRecord? posting,
        MonthlyIssuedInvoiceRecord? issued,
        MonthlyInvoiceCandidateLedgerRecord? frozen,
        MonthlyInvoiceIndependentReviewRecord? issuanceReview,
        DateOnly asOfDate,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence>? allReceipts,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord>? allBankReviews,
        IReadOnlyCollection<OfficialMonthlyReceiptLineAllocationRecord>? allLineAllocations,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord>? allAdjustmentProposals,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord>? allAdjustmentDecisions)
    {
        if (quote is null || posting is null || issued is null ||
            frozen is null || issuanceReview is null)
            return Denied("OfficialSourceMonthlyInvoiceMissing");
        if (asOfDate == default || allReceipts is null ||
            allBankReviews is null || allLineAllocations is null ||
            allAdjustmentProposals is null || allAdjustmentDecisions is null)
            return Denied("CompleteSourceInvoiceAndReceiptHistoryRequired");

        try
        {
            // A constructed quote is not evidence of a signed price.
            var canonical = RoomChangeDailyRentQuoteCalculator.Calculate(
                quote.TransferDate, quote.SourceFullMonthlyRentVnd,
                quote.TargetFullMonthlyRentVnd, quote.TransferDayOwner,
                quote.Rounding, quote.SignedRuleReference);
            if (quote != canonical)
                return Denied("ForgedSourceRentQuote");

            // Verify archived physical/service lines AND real issuance.
            posting.VerifyMonthly(issued, frozen, issuanceReview);
            var original = frozen.VerifyStoredEvidence();
            if (posting.Kind != OfficialInvoiceKind.Monthly ||
                posting.Id == Guid.Empty ||
                posting.IssuedInvoiceId != issued.Id ||
                posting.ContractId != frozen.ContractId ||
                posting.RoomId != frozen.RoomId ||
                posting.BuildingId != frozen.BuildingId ||
                quote.TransferDate.Year != frozen.BillingYear ||
                quote.TransferDate.Month != frozen.BillingMonth ||
                frozen.RentAmount != quote.SourceFullMonthlyRentVnd ||
                frozen.RentAmount != original.RentAmount ||
                asOfDate < quote.TransferDate)
                return Denied("IssuedMonthlySourceOrTariffDoesNotMatchTransfer");

            // An accepted adjustment to the full monthly invoice cannot
            // silently be counted as rent. Pending, rejected or orphaned
            // correction history is also HOLD until complete line-level
            // classification and independent approval exists.
            if (allAdjustmentProposals.Count != 0 ||
                allAdjustmentDecisions.Count != 0)
                return Denied("UnclassifiedMonthlyCorrectionCannotBeRent");

            var localZone = TimeZoneInfo.FindSystemTimeZoneById(
                "Asia/Ho_Chi_Minh");
            var postedOn = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(
                    posting.PostedAtUtc, localZone));
            if (postedOn > asOfDate)
                return Denied("MonthlyInvoicePostedAfterReconciliationDate");

            // It is essential to validate ALL receipt/review history first,
            // not only rent lines. A receipt allocated to multiple categories
            // or pending bank review makes the whole view untrustworthy.
            var lines = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
                posting, frozen, asOfDate,
                allReceipts, allBankReviews, allLineAllocations);
            if (!lines.CanClassifyRemainingDebt ||
                lines.PaidRentVnd is null ||
                lines.PaidElectricityVnd is null ||
                lines.PaidWaterVnd is null ||
                lines.PaidServiceVnd is null)
                return Denied("IncompleteOrUnverifiedSourceReceiptAllocation");

            var balance = OfficialInvoiceReceivableBalanceEvaluator.Assess(
                posting, asOfDate, 0m, allReceipts, allBankReviews);
            var categorizedPaid = checked(
                lines.PaidRentVnd.Value + lines.PaidElectricityVnd.Value +
                lines.PaidWaterVnd.Value + lines.PaidServiceVnd.Value);
            if (!balance.IsOfficialDebtPosted ||
                balance.IsExternallyVerifiedBankTransfer ||
                balance.AdjustedAmount != frozen.TotalAmount ||
                balance.OriginalAmount != frozen.TotalAmount ||
                balance.PendingOrRejectedPaymentCount != 0 ||
                balance.IndependentlyReviewedManualPayments != categorizedPaid ||
                balance.OutstandingAmount != frozen.TotalAmount - categorizedPaid ||
                lines.PaidRentVnd.Value > original.RentAmount)
                return Denied("SourcePaymentBalanceNotReconciled");

            var paidRent = lines.PaidRentVnd.Value;
            var rentLines = allLineAllocations
                .Where(x => x.Category == ContractDepositOffsetCategory.UnpaidRent)
                .OrderBy(x => x.Id).ToArray();
            if (paidRent > 0m && rentLines.Length == 0)
                return Denied("PaidSourceRentRequiresSignedAllocation");

            string? evidenceReference = null;
            if (paidRent > 0m)
            {
                var canonicalReceiptLines = string.Join("\n",
                    rentLines.Select(x =>
                        x.Id.ToString("D") + "|" +
                        x.ReceiptReviewId.ToString("D") + "|" +
                        x.PaymentId.ToString("D") + "|" +
                        x.Amount.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                        "|" + x.SignedAllocationReference + "|" +
                        x.InvoiceEvidenceSha256));
                evidenceReference =
                    "REVIEWED-RENT-LINE-ALLOCATIONS-SHA256:" +
                    Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(canonicalReceiptLines)))
                        .ToLowerInvariant();
            }

            var snapshot = new RoomChangeSourceRentSnapshot(
                frozen.ContractId, frozen.BillingYear, frozen.BillingMonth,
                issued.Id, original.RentAmount, 0m, paidRent,
                frozen.FrozenEvidenceSha256, evidenceReference);
            var preview = RoomChangeMidMonthRentReconciliation.Assess(
                quote, snapshot);

            return new RoomChangeOfficialSourceRentEvidenceResult(
                snapshot, preview, posting.Id,
                allBankReviews.Count, rentLines.Length,
                Array.Empty<string>());
        }
        catch (Exception e) when (
            e is InvalidOperationException or InvalidDataException or
            ArgumentException or OverflowException)
        {
            // Fail closed and return NO partial financial figures.
            return Denied("OfficialSourceInvoiceEvidenceInconsistent");
        }
    }

    private static RoomChangeOfficialSourceRentEvidenceResult Denied(string reason)
        => new(null, null, null, 0, 0, new[] { reason });
}

/// <summary>
/// A source-verified RENT-ONLY projection. Bank reviews are manual
/// attestation, not bank-gateway confirmation. No issued-line correction
/// or financial/contract writer is authorized.
/// </summary>
public sealed record RoomChangeOfficialSourceRentEvidenceResult(
    RoomChangeSourceRentSnapshot? Snapshot,
    RoomChangeMidMonthRentPreview? Preview,
    Guid? OfficialReceivablePostingId,
    int ReviewedReceiptCount,
    int ReviewedRentLineCount,
    IReadOnlyList<string> Blockers)
{
    public bool CanPreview => Blockers.Count == 0 &&
        Snapshot is not null && Preview is not null;
    public bool CanPostInvoiceAdjustment => false;
    public bool CanExecuteRoomChange => false;
    public bool CanRefundOrCollectCash => false;
    public bool IsBankGatewayVerified => false;
}
