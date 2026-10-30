using System;
using System.IO;
using System.Reflection;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

/// <summary>
/// 2G.4 decisions never send money, modify a contract or offset debt.
/// The 2G.5 handover must consume exactly the already-approved decision.
/// </summary>
public sealed class ContractDepositSettlementDecision_Tests
{
    private static readonly DateTime Issued = new(
        2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Approved = Issued.AddHours(2);

    [Fact]
    public void DEP4_01_Full_Refund_Is_A_Plan_Not_A_Money_Transfer()
    {
        var d = Decision(0m);
        d.VerifyStoredDecision();
        Assert.Equal(3_000_000m, d.VerifiedHeldAmount);
        Assert.Equal(3_000_000m, d.ApprovedRefundAmount);
        Assert.Equal(0m, d.ApprovedForfeitAmount);
        Assert.Equal(0m, d.ApprovedDebtOffsetAmount);
        Assert.False(d.IsRefundExecuted);
        Assert.False(d.IsAutomaticDebtOffset);
        Assert.False(d.IsDepositTransferExecuted);
    }

    [Fact]
    public void DEP4_02_Owner_Can_Document_Damage_And_Approve_Exact_Refund()
    {
        var d = Decision(250_000m, "Cracked window in room 203",
            "PHOTO-SIGNED-DAMAGE-REPORT-203");
        d.VerifyStoredDecision();
        Assert.Equal(2_750_000m, d.ApprovedRefundAmount);
        d.RequireForCloseout(d.ContractId, d.MoveOutRequestId,
            d.FinalIssuedInvoiceId, d.BuildingId, 3_000_000m,
            250_000m, "Cracked window in room 203",
            "PHOTO-SIGNED-DAMAGE-REPORT-203", "SIGNED-DEPOSIT-POLICY-001");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3000001)]
    [InlineData(1.5)]
    public void DEP4_03_Invalid_Deduction_Always_Fails(decimal amount)
    {
        Assert.Throws<InvalidOperationException>(() => Decision(amount,
            "Documented property damage", "SIGNED-PHOTO-DAMAGE-001"));
    }

    [Fact]
    public void DEP4_04_Unpaid_Debt_Cannot_Be_Silently_Deducted_From_Deposit()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositSettlementDecisionRecord.Approve(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Approved, Issued,
                3_000_000m, 400_000m, 0m, null, null,
                "SIGNED-DEPOSIT-POLICY-001"));
    }

    [Fact]
    public void DEP4_05_Damage_Deduction_Requires_Reason_And_Evidence()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Decision(250_000m, null, "PHOTO-SIGNED-DAMAGE-REPORT-203"));
        Assert.Throws<InvalidOperationException>(() =>
            Decision(250_000m, "Cracked window in room 203", null));
        Assert.Throws<InvalidOperationException>(() =>
            Decision(0m, "Invented damage note", null));
    }

    [Fact]
    public void DEP4_06_Closeout_Cannot_Change_Deduction_Or_Policy()
    {
        var d = Decision(250_000m, "Cracked window in room 203",
            "PHOTO-SIGNED-DAMAGE-REPORT-203");
        Assert.Throws<InvalidOperationException>(() =>
            d.RequireForCloseout(d.ContractId, d.MoveOutRequestId,
                d.FinalIssuedInvoiceId, d.BuildingId, 3_000_000m,
                0m, null, null, "SIGNED-DEPOSIT-POLICY-001"));
        Assert.Throws<InvalidOperationException>(() =>
            d.RequireForCloseout(d.ContractId, d.MoveOutRequestId,
                d.FinalIssuedInvoiceId, d.BuildingId, 3_000_000m,
                250_000m, "Cracked window in room 203",
                "PHOTO-SIGNED-DAMAGE-REPORT-203",
                "OTHER-DEPOSIT-POLICY-002"));
    }

    [Theory]
    [InlineData("ApprovedRefundAmount")]
    [InlineData("VerifiedHeldAmount")]
    [InlineData("ApprovedDebtOffsetAmount")]
    [InlineData("ApprovedForfeitAmount")]
    public void DEP4_07_Tampered_Money_Columns_Cannot_Replay_Valid_Hash(
        string property)
    {
        var d = Decision(250_000m, "Cracked window in room 203",
            "PHOTO-SIGNED-DAMAGE-REPORT-203");
        typeof(ContractDepositSettlementDecisionRecord)
            .GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(d, 123m);
        Assert.Throws<InvalidDataException>(d.VerifyStoredDecision);
    }

    [Fact]
    public void DEP4_08_Rewritten_Proof_Is_Rejected()
    {
        var d = Decision();
        typeof(ContractDepositSettlementDecisionRecord)
            .GetProperty(nameof(d.ApprovalJson))!
            .SetValue(d, d.ApprovalJson + " ");
        Assert.Throws<InvalidDataException>(d.VerifyStoredDecision);
    }

    [Fact]
    public void DEP4_09_Approval_Time_Must_Not_Precede_Issued_Invoice()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositSettlementDecisionRecord.Approve(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Issued.AddTicks(-1), Issued,
                3_000_000m, 0m, 0m,
                null, null, "SIGNED-DEPOSIT-POLICY-001"));
    }

    private static ContractDepositSettlementDecisionRecord Decision(
        decimal amount = 0m, string? reason = null, string? proof = null)
        => ContractDepositSettlementDecisionRecord.Approve(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Approved, Issued,
            3_000_000m, 0m, amount, reason, proof, "SIGNED-DEPOSIT-POLICY-001");
}
