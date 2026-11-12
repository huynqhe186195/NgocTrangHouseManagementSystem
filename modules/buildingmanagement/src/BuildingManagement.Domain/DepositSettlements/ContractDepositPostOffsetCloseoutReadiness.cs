using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.Invoices;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Financial facts for a POSTED internal OI-04 credit. Separates the
/// accounting event from subsequent proof of final bill, paid residual
/// debt, physical handover, and actually executed refund. No money action.
/// </summary>
public sealed record ContractDepositPostOffsetCloseoutReadiness(
    Guid ContractId, Guid MoveOutRequestId, Guid PostingId,
    DateOnly AsOfDate, decimal VerifiedOriginalHeldDepositVnd,
    decimal PostedInternalDepositDebitVnd, decimal RemainingHeldDepositVnd,
    decimal OfficialAdjustedReceivablesVnd,
    decimal IndependentlyReviewedBankReceiptsVnd,
    decimal PostedInternalDebtCreditVnd,
    decimal OfficialOutstandingDebtVnd,
    bool HasSignedFinalInvoice,
    int SignedChecklistTaskCount, int RequiredChecklistTaskCount,
    IReadOnlyList<string> Blockers)
{
    public bool IsLedgerBalanced => !Blockers.Contains("PostedOffsetLedgerMismatch");
    public bool HasUnpaidOfficialDebt => OfficialOutstandingDebtVnd > 0m;
    public bool RequiresRefundVerification => RemainingHeldDepositVnd > 0m;
    // Owner may approve a PLAN once every authoritative debt and source is
    // reconciled. Executing the planned refund still needs actual bank proof,
    // and physical completion remains an independent 2G.5 commit gate.
    public bool CanApproveDepositDisposition =>
        HasSignedFinalInvoice && IsLedgerBalanced && !HasUnpaidOfficialDebt &&
        !Blockers.Any(x => x is not
            "ResidualDepositRequiresExecutedBankRefundEvidence" and
            not "SignedMoveOutChecklistIncomplete" and
            not "SeparateOwnerFinalCloseoutApprovalRequired");

    public bool IsFullyFinanciallyReconciled =>
        HasSignedFinalInvoice && IsLedgerBalanced &&
        !HasUnpaidOfficialDebt && !RequiresRefundVerification &&
        !Blockers.Contains("DepositCashSourceNotVerified") &&
        !Blockers.Contains("OfficialInvoiceHistoryNotComplete") &&
        !Blockers.Contains("PendingOrRejectedBankReceiptsNeedReview") &&
        !Blockers.Contains("AcceptedMonthlyBankReceiptMissingSignedFeeSplit") &&
        !Blockers.Contains("InternalOffsetNotYetEffectiveAsOfDate");
    // The final transfer, bank refund and contract termination are NOT
    // authorized by a source-only reconciliation read.
    public bool CanExecuteRefund => false;
    public bool CanCompleteMoveOut => false;
}

public static class ContractDepositPostOffsetCloseoutEvaluator
{
    public static ContractDepositPostOffsetCloseoutReadiness Assess(
        ContractDepositDebtOffsetPostingRecord posted,
        ContractDepositDebtOffsetApprovalRecord approval,
        ContractDepositLedgerReview deposit,
        DateOnly asOfDate,
        IReadOnlyCollection<OfficialInvoiceReceivableBalance> allBalances,
        int issuedFinalInvoiceCount,
        int expectedIssuedInvoiceCount,
        int signedChecklistTaskCount,
        int requiredChecklistTaskCount,
        bool allAcceptedMonthlyReceiptsHaveSignedFeeSplits = true)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(deposit);
        ArgumentNullException.ThrowIfNull(allBalances);
        posted.VerifyStoredPosting(approval);
        if (asOfDate == default || issuedFinalInvoiceCount < 0 ||
            expectedIssuedInvoiceCount < 0 ||
            signedChecklistTaskCount < 0 || requiredChecklistTaskCount < 0 ||
            signedChecklistTaskCount > requiredChecklistTaskCount)
            throw new ArgumentException("Invalid closeout reconciliation inputs.");

        var blockers = new List<string>();
        var balances = allBalances.ToArray();
        if (balances.Any(x => x is null || x.ContractId != posted.ContractId ||
            x.AsOfDate != asOfDate || !x.IsOfficialDebtPosted ||
            x.OriginalAmount <= 0m ||
            decimal.Truncate(x.OriginalAmount) != x.OriginalAmount ||
            decimal.Truncate(x.ApprovedSignedAdjustment) != x.ApprovedSignedAdjustment ||
            x.AdjustedAmount <= 0m ||
            x.AdjustedAmount != x.OriginalAmount + x.ApprovedSignedAdjustment ||
            decimal.Truncate(x.AdjustedAmount) != x.AdjustedAmount ||
            x.IndependentlyReviewedManualPayments < 0m ||
            decimal.Truncate(x.IndependentlyReviewedManualPayments) !=
                x.IndependentlyReviewedManualPayments ||
            x.OutstandingAmount < 0m ||
            decimal.Truncate(x.OutstandingAmount) != x.OutstandingAmount ||
            x.InternalDepositOffsetVnd < 0m ||
            decimal.Truncate(x.InternalDepositOffsetVnd) !=
                x.InternalDepositOffsetVnd ||
            x.AdjustedAmount != x.IndependentlyReviewedManualPayments +
                x.InternalDepositOffsetVnd + x.OutstandingAmount ||
            x.AcceptedPaymentCount < 0 || x.PendingOrRejectedPaymentCount < 0 ||
            (x.OutstandingAmount == 0m
                ? x.Status != IssuedInvoicePaymentStatus.Paid
                : x.Status == IssuedInvoicePaymentStatus.Paid) ||
            (x.Kind == OfficialInvoiceKind.Final &&
                (x.ApprovedSignedAdjustment != 0m || x.InternalDepositOffsetVnd != 0m))) ||
            balances.Select(x => x.ReceivablePostingId).Distinct().Count() !=
            balances.Length ||
            balances.Length == 0 ||
            balances.Length != expectedIssuedInvoiceCount ||
            balances.Count(x => x.Kind == OfficialInvoiceKind.Final) !=
                issuedFinalInvoiceCount)
            blockers.Add("OfficialInvoiceHistoryNotComplete");

        var adjusted = balances.Sum(x => x.AdjustedAmount);
        var bank = balances.Sum(x => x.IndependentlyReviewedManualPayments);
        var internalCredit = balances.Sum(x => x.InternalDepositOffsetVnd);
        var unpaid = balances.Sum(x => x.OutstandingAmount);
        // The Owner snapshot freezes the monthly debt balance AT POSTING.
        // Independently reviewed bank receipts AFTER posting may reduce it,
        // while a separately issued FINAL invoice can add legitimate new
        // debt. Comparing the mutable live total with the frozen snapshot
        // for exact equality would make debt collection impossible.
        var remainingMonthly = balances
            .Where(x => x.Kind == OfficialInvoiceKind.Monthly)
            .Sum(x => x.OutstandingAmount);
        if (internalCredit != posted.CreditOfficialDebtVnd ||
            posted.VerifiedHeldAmount != posted.DebitHeldDepositVnd +
                posted.RemainingHeldDepositVnd ||
            remainingMonthly > posted.RemainingOfficialDebtVnd ||
            adjusted != bank + internalCredit + unpaid)
            blockers.Add("PostedOffsetLedgerMismatch");

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var postingDay = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(posted.PostedAtUtc, zone));
        if (asOfDate < postingDay)
            blockers.Add("InternalOffsetNotYetEffectiveAsOfDate");

        if (deposit.ContractId != posted.ContractId ||
            deposit.MoveOutRequestId != posted.MoveOutRequestId ||
            deposit.VerifiedIncomingAllocationTotal != posted.VerifiedHeldAmount ||
            deposit.RecordedCompletedOutgoingTotal != 0m ||
            deposit.Blockers.Any(x => x is not
                "FinalInvoiceDebtNotReconciled" and
                not "DepositDeductionPolicyNotApproved" and
                not "RefundAuthorizationAndHandoverMissing"))
            blockers.Add("DepositCashSourceNotVerified");

        if (balances.Any(x => x.PendingOrRejectedPaymentCount != 0))
            blockers.Add("PendingOrRejectedBankReceiptsNeedReview");
        if (!allAcceptedMonthlyReceiptsHaveSignedFeeSplits)
            blockers.Add("AcceptedMonthlyBankReceiptMissingSignedFeeSplit");
        if (unpaid > 0m)
            blockers.Add("OutstandingOfficialDebtRequiresVerifiedCollection");
        if (posted.RemainingHeldDepositVnd > 0m)
            blockers.Add("ResidualDepositRequiresExecutedBankRefundEvidence");
        if (issuedFinalInvoiceCount != 1)
            blockers.Add("OneSignedFinalInvoiceRequired");
        if (signedChecklistTaskCount != requiredChecklistTaskCount ||
            requiredChecklistTaskCount != 5)
            blockers.Add("SignedMoveOutChecklistIncomplete");

        // Even a no-refund / zero-residual case is NOT completion approval:
        // the coordinated end+room disposition SQL commit has its own gate.
        blockers.Add("SeparateOwnerFinalCloseoutApprovalRequired");
        return new(posted.ContractId, posted.MoveOutRequestId, posted.Id,
            asOfDate, posted.VerifiedHeldAmount, posted.DebitHeldDepositVnd,
            posted.RemainingHeldDepositVnd, adjusted, bank, internalCredit,
            unpaid, issuedFinalInvoiceCount == 1,
            signedChecklistTaskCount, requiredChecklistTaskCount,
            blockers.Distinct(StringComparer.Ordinal).ToArray());
    }
}
