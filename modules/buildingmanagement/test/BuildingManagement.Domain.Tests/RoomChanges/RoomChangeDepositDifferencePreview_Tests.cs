using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeDepositDifferencePreview_Tests
{
    private static RoomChangeDepositDifferenceFacts Facts(
        decimal target = 3_000_000m,
        decimal held = 3_000_000m,
        decimal postedDeduction = 0m)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), held, postedDeduction, target,
            "INDEPENDENT-LEDGER-REVIEW-CONTRACT-DEPOSIT",
            "SIGNED-TARGET-REQUIRED-DEPOSIT-RULE-2026",
            postedDeduction > 0m ? "OWNER-SIGNED-POSTED-DEDUCTION-2026" : null,
            HasUnresolvedDispute: false,
            HasEarlierRefundOrCarryForward: false);

    [Fact]
    public void TRF_DEP01_Equal_Actual_Held_And_Required_Has_Only_Carry()
    {
        var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(Facts());
        Assert.Equal(3_000_000m, r.ProposedCarryForwardVnd);
        Assert.Equal(0m, r.UnpaidTopUpRequirementVnd);
        Assert.Equal(0m, r.UnpaidRefundCandidateVnd);
        Assert.Equal(RoomChangeDepositTreatment.CarryAllWithNoDifference,
            r.ProposedPolicyLane);
        Assert.False(r.CanExecuteRoomChange);
    }

    [Fact]
    public void TRF_DEP02_Higher_Target_Requires_Actual_New_Cash_Collection()
    {
        var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(
            Facts(target: 4_000_000m));
        Assert.Equal(3_000_000m, r.ProposedCarryForwardVnd);
        Assert.Equal(1_000_000m, r.UnpaidTopUpRequirementVnd);
        Assert.Equal(0m, r.UnpaidRefundCandidateVnd);
        Assert.Equal(RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
            r.ProposedPolicyLane);
        Assert.False(r.IsCashMovementPosted);
    }

    [Fact]
    public void TRF_DEP03_Lower_Target_Requires_Independent_Refund_Evidence()
    {
        var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(
            Facts(target: 2_000_000m));
        Assert.Equal(2_000_000m, r.ProposedCarryForwardVnd);
        Assert.Equal(0m, r.UnpaidTopUpRequirementVnd);
        Assert.Equal(1_000_000m, r.UnpaidRefundCandidateVnd);
        Assert.Equal(RoomChangeDepositTreatment.PartialCarryWithRefund,
            r.ProposedPolicyLane);
        Assert.False(r.IsIndependentlyApprovedExecution);
    }

    [Fact]
    public void TRF_DEP04_Signed_Posted_Damage_Changes_Only_Remaining_Cash()
    {
        var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(
            Facts(target: 3_000_000m, postedDeduction: 250_000m));
        Assert.Equal(2_750_000m, r.SourceAvailableToDispositionVnd);
        Assert.Equal(2_750_000m, r.ProposedCarryForwardVnd);
        Assert.Equal(250_000m, r.UnpaidTopUpRequirementVnd);
        Assert.Equal(0m, r.UnpaidRefundCandidateVnd);
    }

    [Fact]
    public void TRF_DEP05_Deduction_With_Lower_Requirement_Leaves_Refund_Candidate()
    {
        var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(
            Facts(target: 2_000_000m, postedDeduction: 250_000m));
        Assert.Equal(2_750_000m, r.SourceAvailableToDispositionVnd);
        Assert.Equal(2_000_000m, r.ProposedCarryForwardVnd);
        Assert.Equal(750_000m, r.UnpaidRefundCandidateVnd);
    }

    [Fact]
    public void TRF_DEP06_Never_Offset_Deduction_Above_Cash_Backed_Deposit()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                Facts(postedDeduction: 3_000_001m)));
    }

    [Fact]
    public void TRF_DEP07_SubVnd_And_Negative_Cash_Are_Denied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                Facts(held: -5m)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                Facts(target: 4_000_000.5m)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                Facts(postedDeduction: 0.5m)));
    }

    [Fact]
    public void TRF_DEP08_Open_Dispute_Or_Previous_Payout_Holds_Entire_Preview()
    {
        var initial = Facts();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                initial with { HasUnresolvedDispute = true }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                initial with { HasEarlierRefundOrCarryForward = true }));
    }

    [Fact]
    public void TRF_DEP09_No_Fictitious_Or_Missing_Owner_Deduction_Proof()
    {
        var zero = Facts();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                zero with { OwnerApprovedDeductionReference = "FALSE-PROOF-12345" }));
        var positive = Facts(postedDeduction: 250_000m);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                positive with { OwnerApprovedDeductionReference = null }));
    }

    [Fact]
    public void TRF_DEP10_Source_Target_Must_Not_Resolve_To_Same_Contract()
    {
        var initial = Facts();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                initial with { TargetContractId = initial.SourceContractId }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(null));
    }

    [Fact]
    public void TRF_DEP11_Missing_Cash_Proof_Or_Target_Tariff_Blocks_Preview()
    {
        var initial = Facts();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                initial with { SourceHeldCashLedgerReference = null }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDepositDifferencePreviewCalculator.Calculate(
                initial with { TargetRequiredDepositPolicyReference = "missing" }));
    }

    [Fact]
    public void TRF_DEP12_One_Arithmetic_Split_Cannot_Be_Both_Refund_And_Topup()
    {
        foreach (var target in new[] { 1_000_000m, 3_000_000m, 5_000_000m })
        {
            var r = RoomChangeDepositDifferencePreviewCalculator.Calculate(
                Facts(target: target));
            Assert.Equal(r.SourceAvailableToDispositionVnd,
                r.ProposedCarryForwardVnd + r.UnpaidRefundCandidateVnd);
            Assert.Equal(r.TargetRequiredHeldVnd,
                r.ProposedCarryForwardVnd + r.UnpaidTopUpRequirementVnd);
            Assert.False(r.UnpaidRefundCandidateVnd > 0m &&
                         r.UnpaidTopUpRequirementVnd > 0m);
            Assert.False(r.CanExecuteRoomChange);
        }
    }
}
