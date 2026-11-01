using System;
using System.Linq;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositPostOffsetCloseoutReadiness_Tests
{
    private static readonly Guid Contract = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Building = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly Guid MonthlyPosting = Guid.NewGuid();

    private static (ContractDepositDebtOffsetApprovalRecord Approval,
        ContractDepositDebtOffsetPostingRecord Posting) Signed(decimal held)
    {
        var claim = new ContractDepositOffsetClaim(
            Guid.NewGuid(), ContractDepositOffsetCategory.UnpaidRent,
            1_000_000m, true, false, new string('a',64));
        var approved = ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer,
            DateTime.UtcNow.AddMinutes(-10), held,
            "SIGNED-OI04-POST-RECONCILIATION-001",
            "SIGNED-INDEPENDENT-REVIEW-001", new[]{claim});
        var posted = ContractDepositDebtOffsetPostingRecord.Post(
            Guid.NewGuid(), Owner, DateTime.UtcNow.AddMinutes(-2),
            approved, held, new[]{claim});
        return (approved, posted);
    }

    private static ContractDepositLedgerReview Deposit(decimal incoming)
    {
        var review = new ContractDepositLedgerReview
        {
            ContractId = Contract, MoveOutRequestId = Request
        };
        typeof(ContractDepositLedgerReview)
            .GetProperty(nameof(ContractDepositLedgerReview.VerifiedIncomingAllocationTotal))!
            .SetValue(review, incoming);
        typeof(ContractDepositLedgerReview)
            .GetProperty(nameof(ContractDepositLedgerReview.RecordedCompletedOutgoingTotal))!
            .SetValue(review, 0m);
        review.Blockers.AddRange(new[]
        {
            "FinalInvoiceDebtNotReconciled",
            "DepositDeductionPolicyNotApproved",
            "RefundAuthorizationAndHandoverMissing"
        });
        return review;
    }

    private static DateOnly DateFor(ContractDepositDebtOffsetPostingRecord post)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            post.PostedAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")));

    private static OfficialInvoiceReceivableBalance Balance(
        decimal credit, decimal unpaid = 0m) =>
        new(MonthlyPosting, Guid.NewGuid(), Contract, OfficialInvoiceKind.Monthly,
            1_000_000m, 0m, 1_000_000m,
            1_000_000m-credit-unpaid, unpaid,
            1, 0, unpaid == 0m ? IssuedInvoicePaymentStatus.Paid :
                IssuedInvoicePaymentStatus.PartiallyPaid,
            // Overridden with actual posting date by callers below.
            new DateOnly(2026,11,1), true, false)
        { InternalDepositOffsetVnd = credit };

    private static OfficialInvoiceReceivableBalance Monthly(
        ContractDepositDebtOffsetPostingRecord post, decimal credit = 1_000_000m,
        decimal unpaid = 0m) => Balance(credit, unpaid) with { AsOfDate = DateFor(post) };

    private static OfficialInvoiceReceivableBalance Final(
        ContractDepositDebtOffsetPostingRecord post) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Contract, OfficialInvoiceKind.Final,
            100_000m, 0m, 100_000m, 100_000m, 0m,
            1, 0, IssuedInvoicePaymentStatus.Paid, DateFor(post),
            true, false);

    private static ContractDepositPostOffsetCloseoutReadiness Evaluate(
        ContractDepositDebtOffsetApprovalRecord approval,
        ContractDepositDebtOffsetPostingRecord post,
        ContractDepositLedgerReview deposit,
        OfficialInvoiceReceivableBalance[] balance,
        int issuedFinal = 0, int checklistDone = 0,
        int? issuedTotal = null) =>
        ContractDepositPostOffsetCloseoutEvaluator.Assess(
            post, approval, deposit, DateFor(post), balance,
            issuedFinal, issuedTotal ?? balance.Length, checklistDone, 5);

    [Fact]
    public void OI04READY01_Offset_Is_Not_Refund_Or_Complete_With_Remaining_Held()
    {
        var (a,p) = Signed(2_000_000m);
        var result=Evaluate(a,p,Deposit(2_000_000m),new[]{Monthly(p)});
        Assert.Equal(1_000_000m,result.PostedInternalDebtCreditVnd);
        Assert.Equal(1_000_000m,result.RemainingHeldDepositVnd);
        Assert.Equal(0m,result.OfficialOutstandingDebtVnd);
        Assert.True(result.RequiresRefundVerification);
        Assert.Contains("ResidualDepositRequiresExecutedBankRefundEvidence",result.Blockers);
        Assert.Contains("OneSignedFinalInvoiceRequired",result.Blockers);
        Assert.False(result.CanExecuteRefund);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY02_Zero_Cash_Residual_Still_Requires_Signed_Final_And_Handover()
    {
        var (a,p) = Signed(1_000_000m);
        var result=Evaluate(a,p,Deposit(1_000_000m),new[]{Monthly(p)});
        Assert.Equal(0m,result.RemainingHeldDepositVnd);
        Assert.False(result.IsFullyFinanciallyReconciled);
        Assert.DoesNotContain("ResidualDepositRequiresExecutedBankRefundEvidence",
            result.Blockers);
        Assert.Contains("OneSignedFinalInvoiceRequired",result.Blockers);
        Assert.Contains("SignedMoveOutChecklistIncomplete",result.Blockers);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY03_Another_Official_Debt_Blocks_Closeout()
    {
        var (a,p) = Signed(1_000_000m);
        var extra = new OfficialInvoiceReceivableBalance(
            Guid.NewGuid(), Guid.NewGuid(), Contract, OfficialInvoiceKind.Monthly,
            83_000m, 0m, 83_000m, 0m, 83_000m, 0, 0,
            IssuedInvoicePaymentStatus.Unpaid, DateFor(p), true, false);
        var result=Evaluate(a,p,Deposit(1_000_000m),new[]{Monthly(p),extra});
        Assert.Contains("PostedOffsetLedgerMismatch",result.Blockers);
        Assert.Contains("OutstandingOfficialDebtRequiresVerifiedCollection",result.Blockers);
        Assert.False(result.IsFullyFinanciallyReconciled);
    }

    [Fact]
    public void OI04READY04_Unposted_Invoice_And_Unverified_Cash_Invalidate_Balance()
    {
        var (a,p) = Signed(1_000_000m);
        var result=Evaluate(a,p,Deposit(999_999m),new[]{Monthly(p)},
            issuedTotal:2);
        Assert.Contains("OfficialInvoiceHistoryNotComplete",result.Blockers);
        Assert.Contains("DepositCashSourceNotVerified",result.Blockers);
        Assert.False(result.IsFullyFinanciallyReconciled);
    }

    [Fact]
    public void OI04READY05_Paid_Final_Invoice_And_Signed_Handover_Still_Need_Separate_Closeout()
    {
        var (a,p) = Signed(1_000_000m);
        var result=Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p),Final(p)},issuedFinal:1,checklistDone:5);
        Assert.Equal(1_100_000m,result.OfficialAdjustedReceivablesVnd);
        Assert.Equal(100_000m,result.IndependentlyReviewedBankReceiptsVnd);
        Assert.Equal(1_000_000m,result.PostedInternalDebtCreditVnd);
        Assert.Equal(0m,result.OfficialOutstandingDebtVnd);
        Assert.True(result.IsFullyFinanciallyReconciled);
        Assert.Equal(new[]{"SeparateOwnerFinalCloseoutApprovalRequired"},result.Blockers);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY06_Balance_Must_Reject_Misreported_Offset_Or_Receipt()
    {
        var (a,p) = Signed(1_000_000m);
        var forged = Monthly(p) with { InternalDepositOffsetVnd = 900_000m };
        var result=Evaluate(a,p,Deposit(1_000_000m),new[]{forged});
        Assert.Contains("OfficialInvoiceHistoryNotComplete",result.Blockers);
        Assert.Contains("PostedOffsetLedgerMismatch",result.Blockers);
    }

    [Fact]
    public void OI04READY08_Independently_Reviewed_Bank_Receipt_Must_Have_Signed_Fee_Split()
    {
        var (a,p)=Signed(1_000_000m);
        var result=ContractDepositPostOffsetCloseoutEvaluator.Assess(
            p,a,Deposit(1_000_000m),DateFor(p),
            new[]{Monthly(p),Final(p)},1,2,5,5,
            allAcceptedMonthlyReceiptsHaveSignedFeeSplits:false);
        Assert.Contains("AcceptedMonthlyBankReceiptMissingSignedFeeSplit",
            result.Blockers);
        Assert.False(result.IsFullyFinanciallyReconciled);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY09_Final_Invoice_Is_Mandatory_Even_With_Zero_Debt_And_Zero_Refund()
    {
        var (a,p) = Signed(1_000_000m);
        var noFinal = Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p)},checklistDone:5);
        Assert.True(noFinal.IsLedgerBalanced);
        Assert.False(noFinal.HasSignedFinalInvoice);
        Assert.False(noFinal.IsFullyFinanciallyReconciled);
        Assert.Contains("OneSignedFinalInvoiceRequired",noFinal.Blockers);

        var withFinal = Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p),Final(p)},issuedFinal:1,checklistDone:5);
        Assert.True(withFinal.IsFullyFinanciallyReconciled);
        Assert.False(withFinal.CanExecuteRefund);
        Assert.False(withFinal.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY10_Forged_Adjustment_Cannot_Pass_As_Complete_Invoice_History()
    {
        var (a,p) = Signed(1_000_000m);
        var forged = Final(p) with { ApprovedSignedAdjustment = 10_000m };
        var result = Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p),forged},issuedFinal:1,checklistDone:5);
        Assert.Contains("OfficialInvoiceHistoryNotComplete",result.Blockers);
        Assert.False(result.IsFullyFinanciallyReconciled);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04READY11_Invalid_Receipt_Count_Or_Paid_Status_Fails_Closed()
    {
        var (a,p) = Signed(1_000_000m);
        var badCount = Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p),Final(p) with { AcceptedPaymentCount = -1 }},
            issuedFinal:1,checklistDone:5);
        Assert.Contains("OfficialInvoiceHistoryNotComplete",badCount.Blockers);
        Assert.False(badCount.IsFullyFinanciallyReconciled);

        var badStatus = Evaluate(a,p,Deposit(1_000_000m),
            new[]{Monthly(p),Final(p) with { Status = IssuedInvoicePaymentStatus.Unpaid }},
            issuedFinal:1,checklistDone:5);
        Assert.Contains("OfficialInvoiceHistoryNotComplete",badStatus.Blockers);
        Assert.False(badStatus.IsFullyFinanciallyReconciled);
    }

    [Fact]
    public void OI04READY07_External_Bank_Transfer_Cannot_Be_Fabricated()
    {
        var (a,p)=Signed(2_000_000m);
        var result=Evaluate(a,p,Deposit(2_000_000m),new[]{Monthly(p)});
        Assert.False(result.CanExecuteRefund);
        Assert.True(result.RequiresRefundVerification);
        Assert.False(result.CanCompleteMoveOut);
    }
}
