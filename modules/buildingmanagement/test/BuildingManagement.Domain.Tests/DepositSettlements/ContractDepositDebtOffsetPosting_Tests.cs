using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositDebtOffsetPosting_Tests
{
    private static readonly Guid Contract = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Building = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly DateTime ApprovedAt =
        new(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc);

    private static ContractDepositOffsetClaim Claim(int id,
        ContractDepositOffsetCategory category, decimal amount)
        => new(Guid.Parse($"20000000-0000-4000-8000-{id:000000000000}"),
            category, amount, true, false, "SIGNED-OFFICIAL-POSTING-SHA-001");

    private static ContractDepositDebtOffsetApprovalRecord Approve(
        decimal held, params ContractDepositOffsetClaim[] claims)
        => ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer,
            ApprovedAt, held, "SIGNED-OWNER-OI04-OFFSET-POLICY-001",
            "SIGNED-STAFF-DEBT-REVIEW-001", claims);

    [Fact]
    public void OI04POST01_Double_Ledger_Legs_Match_Signed_Approval()
    {
        var claims = new[] {
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 700_000m),
            Claim(2, ContractDepositOffsetCategory.Electricity, 100_000m),
            Claim(3, ContractDepositOffsetCategory.Water, 50_000m) };
        var approval = Approve(1_000_000m, claims);
        var posting = ContractDepositDebtOffsetPostingRecord.Post(
            Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(1),
            approval, 1_000_000m, claims.Reverse().ToArray());
        posting.VerifyStoredPosting(approval);
        Assert.Equal(850_000m, posting.DebitHeldDepositVnd);
        Assert.Equal(850_000m, posting.CreditOfficialDebtVnd);
        Assert.Equal(150_000m, posting.RemainingHeldDepositVnd);
        Assert.Equal(0m, posting.RemainingOfficialDebtVnd);
        Assert.Equal(850_000m,
            posting.ReadVerifiedLines(approval).Sum(x => x.ProposedAppliedVnd));
        Assert.True(posting.IsInternalOffsetPosted);
        Assert.False(posting.IsExternallyVerifiedBankTransfer);
        Assert.False(posting.CanRefund);
        Assert.False(posting.CanCompleteMoveOut);
    }

    [Fact]
    public void OI04POST02_Partial_Coverage_Retains_Unpaid_Claim()
    {
        var claims = new[] {
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 500_000m) };
        var approval = Approve(200_000m, claims);
        var posting = ContractDepositDebtOffsetPostingRecord.Post(
            Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(1),
            approval, 200_000m, claims);
        Assert.Equal(200_000m, posting.CreditOfficialDebtVnd);
        Assert.Equal(300_000m, posting.RemainingOfficialDebtVnd);
        Assert.Equal(0m, posting.RemainingHeldDepositVnd);
    }

    [Fact]
    public void OI04POST03_Revoked_Or_Changed_Evidence_Is_Never_Posted()
    {
        var claim = Claim(1, ContractDepositOffsetCategory.UnpaidRent, 500_000m);
        var approval = Approve(200_000m, claim);
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetPostingRecord.Post(
                Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(1),
                approval, 201_000m, new[] { claim }));
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetPostingRecord.Post(
                Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(1),
                approval, 200_000m,
                new[] { claim with { OutstandingVnd = 499_999m } }));
    }

    [Fact]
    public void OI04POST04_Different_Actor_Or_Earlier_Timestamp_Denied()
    {
        var claim = Claim(1, ContractDepositOffsetCategory.UnpaidRent, 100_000m);
        var approval = Approve(200_000m, claim);
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetPostingRecord.Post(
                Guid.NewGuid(), Reviewer, ApprovedAt.AddMinutes(1),
                approval, 200_000m, new[] { claim }));
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetPostingRecord.Post(
                Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(-1),
                approval, 200_000m, new[] { claim }));
    }

    [Fact]
    public void OI04POST05_Proof_Or_Balanced_Amounts_Tampering_Fails()
    {
        var claim = Claim(1, ContractDepositOffsetCategory.UnpaidRent, 100_000m);
        var approval = Approve(200_000m, claim);
        var posting = ContractDepositDebtOffsetPostingRecord.Post(
            Guid.NewGuid(), Owner, ApprovedAt.AddMinutes(1),
            approval, 200_000m, new[] { claim });
        typeof(ContractDepositDebtOffsetPostingRecord)
            .GetProperty(nameof(ContractDepositDebtOffsetPostingRecord.CreditOfficialDebtVnd))!
            .GetSetMethod(true)!.Invoke(posting, new object[] { 99_999m });
        Assert.Throws<InvalidDataException>(() => posting.VerifyStoredPosting(approval));
    }
}
