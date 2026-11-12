using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositDebtOffsetApproval_Tests
{
    private static readonly Guid Contract = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Building = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly DateTime When =
        new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);

    private static ContractDepositOffsetClaim Claim(int id,
        ContractDepositOffsetCategory kind, decimal amount,
        bool disputed = false) =>
        new(Guid.Parse($"10000000-0000-4000-8000-{id:000000000000}"),
            kind, amount, true, disputed, "SIGNED-POSTED-INVOICE-ORIGINAL-001");

    private static ContractDepositDebtOffsetApprovalRecord Approve(
        decimal held, params ContractDepositOffsetClaim[] claims) =>
        ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer,
            When, held, "SIGNED-OI04-PRIORITY-REVIEW-001",
            "SIGNED-INDEPENDENT-DEBT-RECONCILIATION-001", claims);

    [Fact]
    public void DEP_AUTH01_Signed_Offset_Uses_Only_Verified_Priority_And_Balances()
    {
        var claims = new[]
        {
            Claim(2, ContractDepositOffsetCategory.Electricity, 100_000m),
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 700_000m),
            Claim(3, ContractDepositOffsetCategory.Water, 50_000m)
        };
        var row = Approve(1_000_000m, claims);
        row.VerifyStoredApproval();
        row.RequireCurrentEvidence(Contract, Request, Building, 1_000_000m,
            claims.Reverse().ToArray());
        Assert.Equal(850_000m, row.ApprovedOffsetAmount);
        Assert.Equal(150_000m, row.ExpectedRemainingDeposit);
        Assert.Equal(0m, row.ExpectedUncoveredDebt);
        Assert.False(row.IsOffsetPosted);
        Assert.False(row.IsBankRefundCompleted);
    }

    [Fact]
    public void DEP_AUTH02_Insufficient_Held_Deposit_Records_Uncovered_Debt()
    {
        var row = Approve(200_000m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 500_000m));
        Assert.Equal(200_000m, row.ApprovedOffsetAmount);
        Assert.Equal(300_000m, row.ExpectedUncoveredDebt);
        Assert.Equal(0m, row.ExpectedRemainingDeposit);
    }

    [Fact]
    public void DEP_AUTH03_Disputed_Or_Unverified_Claim_Denied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Approve(600_000m, Claim(1,
                ContractDepositOffsetCategory.UnpaidRent, 200_000m, true)));
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetApprovalRecord.Approve(
                Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer,
                When, 600_000m, "SIGNED-OI04-PRIORITY-REVIEW-001",
                "SIGNED-INDEPENDENT-DEBT-RECONCILIATION-001",
                new [] { new ContractDepositOffsetClaim(
                    Guid.NewGuid(), ContractDepositOffsetCategory.UnpaidRent,
                    200_000m, false, false, null) }));
    }

    [Fact]
    public void DEP_AUTH04_Independent_Reviewer_And_Proper_Signed_Policy_Required()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContractDepositDebtOffsetApprovalRecord.Approve(
                Guid.NewGuid(), Contract, Request, Building, Owner, Owner,
                When, 600_000m, "SIGNED-OI04-PRIORITY-REVIEW-001",
                "SIGNED-INDEPENDENT-DEBT-RECONCILIATION-001",
                new[] { Claim(1, ContractDepositOffsetCategory.UnpaidRent,
                    200_000m) }));
    }

    [Fact]
    public void DEP_AUTH05_No_Claim_And_No_Offset_Cannot_Be_Authorization()
    {
        Assert.Throws<InvalidOperationException>(() => Approve(600_000m));
    }

    [Fact]
    public void DEP_AUTH06_Same_Tier_Ambiguous_Partial_Utility_Denied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Approve(100_000m,
                Claim(1, ContractDepositOffsetCategory.Electricity, 90_000m),
                Claim(2, ContractDepositOffsetCategory.Water, 90_000m)));
    }

    [Fact]
    public void DEP_AUTH09_Owner_Signed_Partial_Tier_Persists_Exact_Individual_Lines()
    {
        var claims = new[]
        {
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 50_000m),
            Claim(2, ContractDepositOffsetCategory.Electricity, 90_000m),
            Claim(3, ContractDepositOffsetCategory.Water, 90_000m)
        };
        var split = new[]
        {
            new ContractDepositOwnerTierAllocation(claims[1].ClaimId, 30_000m),
            new ContractDepositOwnerTierAllocation(claims[2].ClaimId, 50_000m)
        };
        var approval = ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer, When,
            130_000m, "OWNER-SIGNED-EW-SPLIT-POLICY-0001",
            "SIGNED-INDEPENDENT-DEBT-RECONCILIATION-001",claims,split);
        approval.VerifyStoredApproval();
        approval.RequireCurrentEvidence(Contract,Request,Building,
            130_000m,claims.Reverse().ToArray());
        Assert.Equal(130_000m,approval.ApprovedOffsetAmount);
        Assert.Equal(100_000m,approval.ExpectedUncoveredDebt);
        Assert.Equal(split,approval.ReadOwnerTierAllocations()
            .OrderBy(x => x.ClaimId).ToArray());
        Assert.False(approval.IsBankRefundCompleted);
    }

    [Fact]
    public void DEP_AUTH07_Any_Changed_Official_Balance_Invalidates_Signoff()
    {
        var claims = new[] { Claim(1,
            ContractDepositOffsetCategory.UnpaidRent, 200_000m) };
        var row = Approve(600_000m, claims);
        Assert.Throws<InvalidOperationException>(() =>
            row.RequireCurrentEvidence(Contract, Request, Building,
                500_000m, claims));
        Assert.Throws<InvalidOperationException>(() =>
            row.RequireCurrentEvidence(Contract, Request, Building,
                600_000m, new[] { claims[0] with { OutstandingVnd = 150_000m } }));
    }

    [Fact]
    public void DEP_AUTH08_Proof_And_Indexed_Data_Are_Immutable_And_Verifiable()
    {
        var row = Approve(600_000m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 200_000m));
        Assert.Equal(64, row.ApprovalSha256.Length);
        var setter = typeof(ContractDepositDebtOffsetApprovalRecord)
            .GetProperty(nameof(ContractDepositDebtOffsetApprovalRecord.ApprovedOffsetAmount))!
            .GetSetMethod(true)!;
        setter.Invoke(row, new object[] { 201_000m });
        Assert.Throws<InvalidDataException>(row.VerifyStoredApproval);
    }
}
