using System;
using System.IO;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositRefundBankReview_Tests
{
    private static readonly DateTime Issued =
        new(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Approved = Issued.AddHours(1);
    private static readonly DateTime Paid = Approved.AddHours(1);
    private static readonly DateTime Reviewed = Paid.AddHours(1);

    private static ContractDepositSettlementDecisionRecord Decision(
        decimal damage = 250_000m) =>
        ContractDepositSettlementDecisionRecord.Approve(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Approved, Issued,
            3_000_000m, 0m, damage,
            damage == 0m ? null : "Cracked window in room 203",
            damage == 0m ? null : "PHOTO-SIGNED-DAMAGE-REPORT-203",
            "SIGNED-DEPOSIT-POLICY-001", 400_000m);

    private static ContractDepositRefundBankReviewRecord Review(
        ContractDepositSettlementDecisionRecord d, Guid? reviewer = null,
        DateTime? paid = null, DateTime? reviewed = null) =>
        ContractDepositRefundBankReviewRecord.Attest(
            Guid.NewGuid(), d, reviewer ?? Guid.NewGuid(),
            reviewed ?? Reviewed, paid ?? Paid,
            "BANK-REFUND-TRACE-20261101-001",
            "BANK-STATEMENT-APPROVED-20261101-001",
            "SIGNED-INDEPENDENT-REVIEW-20261101-001");

    [Fact]
    public void OI04REF01_Independent_Statement_Attestation_Matches_Signed_Net_Refund()
    {
        var decision = Decision();
        var record = Review(decision);
        record.Verify(decision);
        Assert.False(record.IsBankApiVerified);
        Assert.Equal(2_350_000m, record.VerifiedRefundAmountVnd);
        record.RequireForCloseout(decision,decision.ApprovedByUserId,
            Paid, record.BankTransactionReference,
            record.BankStatementEvidenceReference);
    }

    [Fact]
    public void OI04REF02_Owner_Must_Not_Review_Own_Refund()
    {
        var decision = Decision();
        Assert.Throws<InvalidOperationException>(() =>
            Review(decision, reviewer: decision.ApprovedByUserId));
    }

    [Fact]
    public void OI04REF03_Payment_Before_Owner_Approval_Is_Invalid()
    {
        var decision = Decision();
        Assert.Throws<InvalidOperationException>(() =>
            Review(decision, paid: Issued, reviewed: Reviewed));
    }

    [Fact]
    public void OI04REF04_Review_Before_Bank_Payment_Is_Invalid()
    {
        var decision = Decision();
        Assert.Throws<InvalidOperationException>(() =>
            Review(decision, paid: Paid, reviewed: Approved));
    }

    [Fact]
    public void OI04REF05_Receipt_Or_Paid_Time_Mismatch_Cannot_Close()
    {
        var decision = Decision();
        var review = Review(decision);
        Assert.Throws<InvalidOperationException>(() =>
            review.RequireForCloseout(decision, decision.ApprovedByUserId,
                Paid, "SOME-OTHER-BANK-REFUND-0001",
                review.BankStatementEvidenceReference));
        Assert.Throws<InvalidOperationException>(() =>
            review.RequireForCloseout(decision, decision.ApprovedByUserId,
                Paid.AddSeconds(1), review.BankTransactionReference,
                review.BankStatementEvidenceReference));
        Assert.Throws<InvalidOperationException>(() =>
            review.RequireForCloseout(decision, review.ReviewedByUserId,
                Paid, review.BankTransactionReference,
                review.BankStatementEvidenceReference));
    }

    [Fact]
    public void OI04REF06_Different_Decision_Or_Tampered_Proof_Cannot_Replay()
    {
        var decision = Decision();
        var record = Review(decision);
        Assert.Throws<InvalidDataException>(() => record.Verify(Decision()));
        typeof(ContractDepositRefundBankReviewRecord)
            .GetProperty(nameof(record.BankTransactionReference))!
            .SetValue(record, "FORGED-BANK-TRANSFER-999");
        Assert.Throws<InvalidDataException>(() => record.Verify(decision));
    }

    [Fact]
    public void OI04REF07_Zero_Refund_Cannot_Claim_Bank_Review()
    {
        var decision = ContractDepositSettlementDecisionRecord.Approve(
            Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            Guid.NewGuid(),Guid.NewGuid(),Approved,Issued,
            3_000_000m,0m,2_600_000m,
            "Documented major damage at handover",
            "SIGNED-INSPECTION-REPORT-001",
            "SIGNED-DEPOSIT-POLICY-001",400_000m);
        Assert.Equal(0m,decision.ApprovedRefundAmount);
        Assert.Throws<InvalidOperationException>(() => Review(decision));
    }
}
