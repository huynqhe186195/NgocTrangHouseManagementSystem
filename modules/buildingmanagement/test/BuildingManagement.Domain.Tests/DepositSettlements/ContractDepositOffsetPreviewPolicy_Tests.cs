using System;
using System.Linq;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositOffsetPreviewPolicy_Tests
{
    private static readonly Guid Contract = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Approval = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static ContractDepositOffsetClaim Claim(
        int seed, ContractDepositOffsetCategory category, decimal amount,
        bool verified = true, bool disputed = false,
        string? evidence = "SIGNED-VERIFIED-DEBT-001",
        string? damageReason = null, string? damageEvidence = null)
        => new(Guid.Parse($"00000000-0000-4000-8000-{seed:000000000000}"),
            category, amount, verified, disputed, evidence,
            damageReason, damageEvidence);

    private static ContractDepositOffsetPreview Preview(
        decimal held, params ContractDepositOffsetClaim[] claims)
        => ContractDepositOffsetPreviewPolicy.Assess(
            held, Contract, Approval, true, "SIGNED-OI04-POLICY-001", claims);

    [Fact]
    public void DEP_POL01_Owner_Order_Rent_Utilities_Services_Damage_With_No_Double_Use()
    {
        var a = Preview(1_000_000m,
            Claim(5, ContractDepositOffsetCategory.ApprovedDamage, 200_000m,
                damageReason: "Window broken and documented",
                damageEvidence: "PHOTO-AND-OWNER-REVIEW-001"),
            Claim(4, ContractDepositOffsetCategory.ApprovedService, 300_000m),
            Claim(3, ContractDepositOffsetCategory.Water, 100_000m),
            Claim(2, ContractDepositOffsetCategory.Electricity, 300_000m),
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 500_000m));
        Assert.True(a.IsEvidenceCompleteForPreview);
        Assert.Equal(0m, a.ProposedRefundableVnd);
        Assert.Equal(400_000m, a.ProposedUncoveredDebtVnd);
        Assert.Equal(1_000_000m, a.ProposedAllocations.Sum(x => x.ProposedAppliedVnd));
        Assert.Equal(ContractDepositOffsetCategory.UnpaidRent, a.ProposedAllocations[0].Category);
        Assert.Equal(ContractDepositOffsetCategory.Electricity, a.ProposedAllocations[1].Category);
        Assert.Equal(ContractDepositOffsetCategory.Water, a.ProposedAllocations[2].Category);
        Assert.Equal(100_000m, a.ProposedAllocations[3].ProposedAppliedVnd);
        Assert.Equal(0m, a.ProposedAllocations[4].ProposedAppliedVnd);
        Assert.False(a.CanPostDebtOffset);
        Assert.False(a.CanExecuteRefund);
    }

    [Fact]
    public void DEP_POL02_Unused_Held_Balance_Is_Only_A_Refund_Candidate()
    {
        var a = Preview(3_000_000m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 400_000m),
            Claim(2, ContractDepositOffsetCategory.ApprovedDamage, 250_000m,
                damageReason: "Cracked window in old room",
                damageEvidence: "SIGNED-PHOTO-DAMAGE-001"));
        Assert.Equal(2_350_000m, a.ProposedRefundableVnd);
        Assert.Equal(0m, a.ProposedUncoveredDebtVnd);
        Assert.False(a.CanExecuteRefund);
    }

    [Fact]
    public void DEP_POL03_Disputed_Debt_Denies_Entire_Preview_Not_Just_One_Line()
    {
        var a = Preview(3_000_000m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 200_000m),
            Claim(2, ContractDepositOffsetCategory.ApprovedService, 100_000m,
                disputed: true));
        Assert.False(a.IsEvidenceCompleteForPreview);
        Assert.Contains("UnverifiedOrDisputedDebt", a.Blockers);
        Assert.Null(a.ProposedRefundableVnd);
        Assert.Null(a.ProposedUncoveredDebtVnd);
        Assert.Empty(a.ProposedAllocations);
    }

    [Fact]
    public void DEP_POL04_Unconfirmed_Invoice_And_Missing_Signed_Proof_Deny()
    {
        var a = Preview(3_000_000m,
            Claim(1, ContractDepositOffsetCategory.Electricity, 200_000m,
                verified: false, evidence: null));
        Assert.Contains("UnverifiedOrDisputedDebt", a.Blockers);
        Assert.Empty(a.ProposedAllocations);
    }

    [Fact]
    public void DEP_POL05_Damage_Must_Have_Reason_And_Photo_Or_Report_Reference()
    {
        var noReason = Preview(3_000_000m,
            Claim(1, ContractDepositOffsetCategory.ApprovedDamage, 250_000m,
                damageEvidence: "SIGNED-INSPECTION-001"));
        var noEvidence = Preview(3_000_000m,
            Claim(1, ContractDepositOffsetCategory.ApprovedDamage, 250_000m,
                damageReason: "Broken window recorded"));
        Assert.Contains("OwnerApprovedDamageEvidenceMissing", noReason.Blockers);
        Assert.Contains("OwnerApprovedDamageEvidenceMissing", noEvidence.Blockers);
    }

    [Fact]
    public void DEP_POL06_Zero_Damage_Needs_No_Fictitious_Damage_Claim()
    {
        var a = Preview(3_000_000m);
        Assert.True(a.IsEvidenceCompleteForPreview);
        Assert.Empty(a.ProposedAllocations);
        Assert.Equal(3_000_000m, a.ProposedRefundableVnd);
        Assert.False(a.CanExecuteRefund);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    public void DEP_POL07_Invalid_Vnd_Amounts_Are_Always_Blocked(decimal amount)
    {
        Assert.Contains("VerifiedHeldDepositInvalid",
            Preview(amount).Blockers);
        Assert.Contains("DebtClaimInvalid",
            Preview(3_000_000m,
                Claim(1, ContractDepositOffsetCategory.UnpaidRent, amount)).Blockers);
    }

    [Fact]
    public void DEP_POL08_Missing_Owner_Approval_Or_Policy_Prohibits_Even_Preview()
    {
        var a = ContractDepositOffsetPreviewPolicy.Assess(
            3_000_000m, Contract, Guid.Empty, false, "",
            new[] { Claim(1, ContractDepositOffsetCategory.UnpaidRent, 1_000m) });
        Assert.Contains("OwnerApprovalOrSignedPolicyMissing", a.Blockers);
        Assert.Empty(a.ProposedAllocations);
    }

    [Fact]
    public void DEP_POL09_Duplicate_Claim_Id_And_Null_Evidence_Set_Are_Blocked()
    {
        var c = Claim(1, ContractDepositOffsetCategory.UnpaidRent, 200_000m);
        Assert.Contains("DuplicateDebtClaim", Preview(3_000_000m, c, c).Blockers);
        Assert.Contains("DebtEvidenceNotLoaded",
            ContractDepositOffsetPreviewPolicy.Assess(
                3_000_000m, Contract, Approval, true,
                "SIGNED-OI04-POLICY-001", null).Blockers);
    }

    [Fact]
    public void DEP_POL10_Ordering_Is_Deterministic_For_Unordered_Sources()
    {
        var claims = new[]
        {
            Claim(3, ContractDepositOffsetCategory.Water, 80m),
            Claim(1, ContractDepositOffsetCategory.Electricity, 50m),
            Claim(2, ContractDepositOffsetCategory.Electricity, 60m)
        };
        var a = Preview(200m, claims);
        var b = Preview(200m, claims.Reverse().ToArray());
        Assert.Equal(a.ProposedAllocations, b.ProposedAllocations);
        Assert.Equal(190m, a.ProposedAllocations.Sum(x => x.ProposedAppliedVnd));
        Assert.Equal(10m, a.ProposedRefundableVnd);
    }

    [Fact]
    public void DEP_POL11_Damage_Fields_On_Ordinary_Debt_Reject_Classification()
    {
        var a = Preview(1_000_000m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 100_000m,
                damageReason: "Should not have damage on rent"));
        Assert.Contains("DamageEvidenceOnNonDamageDebt", a.Blockers);
    }

    [Fact]
    public void DEP_POL12_Invalid_Category_Is_Never_Silently_Considered_Service()
    {
        var a = Preview(1_000_000m,
            Claim(1, (ContractDepositOffsetCategory)999, 100_000m));
        Assert.Contains("DebtClaimInvalid", a.Blockers);
    }

    [Fact]
    public void DEP_POL13_Partial_Utilities_Tier_Is_Blocked_Until_Owner_Selects_Split_Rule()
    {
        var a = Preview(100m,
            Claim(1, ContractDepositOffsetCategory.Electricity, 80m),
            Claim(2, ContractDepositOffsetCategory.Water, 70m));
        Assert.Contains("PartialPriorityTierRequiresAllocationRule", a.Blockers);
        Assert.Empty(a.ProposedAllocations);
        Assert.Null(a.ProposedRefundableVnd);
    }

    [Fact]
    public void DEP_POL14_One_Partially_Funded_Claim_Is_Unambiguous()
    {
        var a = Preview(100m,
            Claim(1, ContractDepositOffsetCategory.UnpaidRent, 200m));
        Assert.True(a.IsEvidenceCompleteForPreview);
        Assert.Equal(100m, a.ProposedAllocations[0].ProposedAppliedVnd);
        Assert.Equal(100m, a.ProposedUncoveredDebtVnd);
    }
}
