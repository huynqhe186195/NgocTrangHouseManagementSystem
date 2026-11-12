using System;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeRequestReadiness_Tests
{
    private static readonly Guid Building = Guid.NewGuid();

    private static RoomChangeRequestReadinessFacts Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Building, new DateOnly(2026, 12, 1), new DateOnly(2026, 10, 31),
        RoomChangeRequestStatus.PolicyHold, ContractStatus.Active,
        RoomStatus.Occupied, RoomStatus.Available,
        Building, Building, false, false, false, true);

    [Fact]
    public void TRF19_Uncontested_Open_Request_Is_Reviewable_Not_TransferApproved()
    {
        var result = RoomChangeRequestReadiness.Assess(Valid());
        Assert.True(result.CanContinuePolicyReview);
        Assert.Empty(result.Blockers);
    }

    [Theory]
    [InlineData(RoomChangeRequestStatus.Rejected)]
    [InlineData(RoomChangeRequestStatus.Cancelled)]
    public void TRF20_Terminal_Request_Cannot_Be_Revived(RoomChangeRequestStatus state)
    {
        var result = RoomChangeRequestReadiness.Assess(
            Valid() with { RequestStatus = state });
        Assert.False(result.CanContinuePolicyReview);
        Assert.Contains("RequestNoLongerOpen", result.Blockers);
    }

    [Fact]
    public void TRF21_Live_Target_And_Source_Drift_Block_Review()
    {
        var facts = Valid() with {
            TargetRoomStatus = RoomStatus.Maintenance,
            TargetHasPendingOrReservedReservation = true,
            TargetHasSignedOrActiveContract = true,
            SourceHasPendingOrApprovedMoveOut = true
        };
        var result = RoomChangeRequestReadiness.Assess(facts);
        Assert.False(result.CanContinuePolicyReview);
        Assert.Contains("TargetRoomNoLongerAvailable", result.Blockers);
        Assert.Contains("TargetReservationNowOpen", result.Blockers);
        Assert.Contains("TargetContractNowCommitted", result.Blockers);
        Assert.Contains("SourceMoveOutNowOpen", result.Blockers);
    }

    [Fact]
    public void TRF22_CrossBuilding_EndedContract_And_Elapsed_Date_Are_Blocked()
    {
        var result = RoomChangeRequestReadiness.Assess(
            Valid() with {
                TargetBuildingId = Guid.NewGuid(),
                SourceContractStatus = ContractStatus.Ended,
                RequestedDate = new DateOnly(2026, 10, 1)
            });
        Assert.False(result.CanContinuePolicyReview);
        Assert.Contains("BuildingScopeDrift", result.Blockers);
        Assert.Contains("SourceContractNoLongerActive", result.Blockers);
        Assert.Contains("RequestedDateElapsed", result.Blockers);
    }

    [Fact]
    public void TRF29_Missing_Or_Stale_Roster_Blocks_Policy_Review()
    {
        var result = RoomChangeRequestReadiness.Assess(
            Valid() with { TenantRosterSnapshotCurrent = false });
        Assert.False(result.CanContinuePolicyReview);
        Assert.Contains("TenantRosterSnapshotMissingOrChanged", result.Blockers);
    }

    [Fact]
    public void TRF23_Missing_Or_Duplicate_Identity_Fails_Closed()
    {
        Assert.Contains("CurrentFactsMissing",
            RoomChangeRequestReadiness.Assess(null).Blockers);
        var facts = Valid();
        Assert.Contains("RequestIdentityInvalid",
            RoomChangeRequestReadiness.Assess(
                facts with { TargetRoomId = facts.SourceRoomId }).Blockers);
    }
}
