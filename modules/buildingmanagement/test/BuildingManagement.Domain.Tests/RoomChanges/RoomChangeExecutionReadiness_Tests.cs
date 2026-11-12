using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeExecutionReadiness_Tests
{
    private static RoomChangeExecutionFacts Clean() => new(
        true, true, true, false, true, true, true, true, true,
        true, true, false);

    [Fact]
    public void TRF44_Missing_Source_Facts_Deny()
    {
        var status = RoomChangeExecutionReadiness.Assess(null);
        Assert.False(status.CanExecuteTransfer);
        Assert.Contains("ExecutionFactsMissing", status.Blockers);
    }

    [Fact]
    public void TRF45_Even_A_Clean_Read_Only_Preflight_Cannot_Move_Money_Or_Tenant()
    {
        var status = RoomChangeExecutionReadiness.Assess(Clean());
        Assert.False(status.CanExecuteTransfer);
        Assert.Single(status.Blockers);
        Assert.Equal("AtomicRoomChangeExecutionNotImplemented",
            status.Blockers[0]);
    }

    [Fact]
    public void TRF52_Only_Supported_Server_Proven_Lane_Is_Eligible_For_Root_Commit()
    {
        var result = RoomChangeExecutionReadiness.Assess(
            Clean() with { AtomicWriterRegistered = true });
        Assert.True(result.CanExecuteTransfer);
        Assert.Empty(result.Blockers);
        Assert.Contains("UnsupportedOI07ExecutionPolicyLane",
            RoomChangeExecutionReadiness.Assess(
                Clean() with { ExplicitSupportedOI07PolicyLane = false })
                .Blockers);
        Assert.Contains("TransferMonthOriginalMetersNotBilled",
            RoomChangeExecutionReadiness.Assess(
                Clean() with { TransferMonthPhysicallyBilled = false })
                .Blockers);
    }

    [Fact]
    public void TRF46_All_Financial_Scope_And_Conflicting_Intent_Drift_Is_Reported()
    {
        var facts = Clean() with {
            CurrentRoomChangeReviewable = false,
            IndependentPolicyAcceptedAndVerified = false,
            HandoverOriginalMetersAndSignedReferencesVerified = false,
            OtherOpenIntentForSameTarget = true,
            SourceTransferMonthOfficiallyPosted = false,
            AllSourceInvoicesOfficiallyPostedAndReviewed = false,
            DepositOriginalPaymentsFullyProven = false,
            NoPendingOrOutgoingDepositDisposition = false,
            TargetCommitmentsClear = false
        };
        var status = RoomChangeExecutionReadiness.Assess(facts);
        Assert.False(status.CanExecuteTransfer);
        Assert.Contains("CompetingTargetRoomChangeIntent", status.Blockers);
        Assert.Contains("SourcePhysicalHandoverNotVerified", status.Blockers);
        Assert.Contains("UnreconciledSourceReceivables", status.Blockers);
        Assert.Contains("ContractDepositProvenanceIncomplete", status.Blockers);
        Assert.Contains("DepositPendingOrAlreadyDisposed", status.Blockers);
        Assert.Contains("ApprovedOI07PolicyMissingOrRejected", status.Blockers);
    }

    [Fact]
    public void TRF47_A_Reviewed_OI07_Policy_Alone_Does_Not_Prove_Cash()
    {
        var result = RoomChangeExecutionReadiness.Assess(
            Clean() with {
                SourceTransferMonthOfficiallyPosted = false,
                DepositOriginalPaymentsFullyProven = false
            });
        Assert.Contains("SourceTransferMonthNotOfficiallyPosted", result.Blockers);
        Assert.Contains("ContractDepositProvenanceIncomplete", result.Blockers);
        Assert.False(result.CanExecuteTransfer);
    }
}
