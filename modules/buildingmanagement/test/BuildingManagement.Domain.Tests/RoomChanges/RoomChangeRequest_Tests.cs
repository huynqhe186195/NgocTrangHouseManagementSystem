using System;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeRequest_Tests
{
    private static readonly DateTime Now =
        new(2026, 10, 31, 7, 0, 0, DateTimeKind.Utc);

    private static RoomChangeRequest Submit(
        Guid? source = null, Guid? target = null,
        ContractStatus contract = ContractStatus.Active,
        RoomStatus sourceStatus = RoomStatus.Occupied,
        RoomStatus targetStatus = RoomStatus.Available,
        string reason = "Move to a quieter room")
        => RoomChangeRequest.Submit(Guid.NewGuid(), Guid.NewGuid(),
            source ?? Guid.NewGuid(), target ?? Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 11, 30), reason,
            Guid.NewGuid(), Now, contract, sourceStatus, targetStatus);

    [Fact]
    public void TRF13_Submission_Records_Only_Intent_Not_Approved_Transfer()
    {
        var request = Submit();
        Assert.Equal(RoomChangeRequestStatus.Pending, request.Status);
        Assert.Null(request.ReviewedAtUtc);
        Assert.Null(request.ReviewedByUserId);
        Assert.NotEqual(request.SourceRoomId, request.TargetRoomId);
    }

    [Fact]
    public void TRF14_Source_And_Target_Must_Be_Valid()
    {
        var room = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => Submit(room, room));
        Assert.Throws<InvalidOperationException>(() => Submit(
            contract: ContractStatus.Signed));
        Assert.Throws<InvalidOperationException>(() => Submit(
            sourceStatus: RoomStatus.Available));
        Assert.Throws<InvalidOperationException>(() => Submit(
            targetStatus: RoomStatus.Maintenance));
        Assert.Throws<InvalidOperationException>(() => Submit(reason: "bad"));
    }

    [Fact]
    public void TRF15_Review_Hold_Is_Not_Transfer_Approval()
    {
        var request = Submit();
        request.HoldForPolicy(Guid.NewGuid(), Now, "Pending OI-07 business decisions");
        Assert.Equal(RoomChangeRequestStatus.PolicyHold, request.Status);
        Assert.Throws<InvalidOperationException>(() =>
            request.HoldForPolicy(Guid.NewGuid(), Now, "Another review"));
    }

    [Fact]
    public void TRF16_Rejection_Preserves_History_And_Is_Terminal()
    {
        var request = Submit();
        request.HoldForPolicy(Guid.NewGuid(), Now, "Awaiting review of deposit policy");
        request.Reject(Guid.NewGuid(), Now, "Target room is not acceptable");
        Assert.Equal(RoomChangeRequestStatus.Rejected, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Cancel(Guid.NewGuid(), Now));
    }

    [Fact]
    public void TRF17_Cancel_From_Pending_Or_Hold_Only()
    {
        var request = Submit();
        request.Cancel(Guid.NewGuid(), Now);
        Assert.Equal(RoomChangeRequestStatus.Cancelled, request.Status);
        Assert.Throws<InvalidOperationException>(() =>
            request.Reject(Guid.NewGuid(), Now, "Too late to reject"));
        var held = Submit();
        held.HoldForPolicy(Guid.NewGuid(), Now, "Awaiting owner authorization");
        held.Cancel(Guid.NewGuid(), Now);
        Assert.Equal(RoomChangeRequestStatus.Cancelled, held.Status);
    }

    [Fact]
    public void TRF18_Review_Requires_Actor_Proof_And_Utc()
    {
        var request = Submit();
        Assert.Throws<InvalidOperationException>(() =>
            request.Reject(Guid.Empty, Now, "Documented rejection"));
        Assert.Throws<InvalidOperationException>(() =>
            request.HoldForPolicy(Guid.NewGuid(),
                DateTime.SpecifyKind(Now, DateTimeKind.Local), "Documented hold"));
        Assert.Equal(RoomChangeRequestStatus.Pending, request.Status);
    }
}
