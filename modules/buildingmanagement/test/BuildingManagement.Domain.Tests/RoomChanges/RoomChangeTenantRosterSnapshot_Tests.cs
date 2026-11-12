using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeTenantRosterSnapshot_Tests
{
    private static readonly DateTime Now = new(2026, 10, 31, 8, 0, 0,
        DateTimeKind.Utc);
    private static RoomChangeRequest Request() =>
        RoomChangeRequest.Submit(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 12, 31), "Documented transfer request",
            Guid.NewGuid(), Now, ContractStatus.Active, RoomStatus.Occupied,
            RoomStatus.Available);
    private static RoomChangeRosterMember[] Roster() => new[]
    {
        new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.PrimaryTenant),
        new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.Member)
    };

    [Fact]
    public void TRF24_Captured_Roster_Replays_Exactly_With_Stable_Fingerprint()
    {
        var request = Request();
        var members = Roster();
        var captured = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, members, Guid.NewGuid(), Now.AddSeconds(3));
        Assert.Equal(2, captured.RosterCount);
        Assert.Equal(64, captured.RosterSha256.Length);
        Assert.Equal(2, captured.ReadVerifiedRoster().Count);
        Assert.True(captured.MatchesCurrentRoster(request.Id,
            request.SourceContractId, request.SourceRoomId, request.BuildingId,
            new[] { members[1], members[0] }));
        Assert.False(captured.MatchesCurrentRoster(request.Id,
            request.SourceContractId, request.SourceRoomId, Guid.NewGuid(),
            members));
    }

    [Fact]
    public void TRF25_Missing_Duplicate_Or_Invalid_Primary_Are_Denied()
    {
        var request = Request();
        var primary = new RoomChangeRosterMember(
            Guid.NewGuid(), ContractTenantRole.PrimaryTenant);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), request, new[] { primary, primary },
                Guid.NewGuid(), Now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), request, new[] {
                    new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.Member)
                }, Guid.NewGuid(), Now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), request, Array.Empty<RoomChangeRosterMember>(),
                Guid.NewGuid(), Now.AddSeconds(1)));
    }

    [Fact]
    public void TRF26_Terminal_Request_And_Local_Time_Cannot_Capture()
    {
        var request = Request();
        request.Reject(Guid.NewGuid(), Now.AddSeconds(1),
            "Rejected before snapshot capture");
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), request, Roster(),
                Guid.NewGuid(), Now.AddSeconds(3)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), Request(), Roster(), Guid.NewGuid(),
                DateTime.SpecifyKind(Now.AddSeconds(1), DateTimeKind.Local)));
    }

    [Fact]
    public void TRF27_Party_Changes_Invalidate_Stored_Snapshot()
    {
        var request = Request();
        var party = Roster();
        var record = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, party, Guid.NewGuid(), Now.AddSeconds(1));
        Assert.False(record.MatchesCurrentRoster(request.Id,
            request.SourceContractId, request.SourceRoomId, request.BuildingId,
            new[] { party[0] }));
        Assert.False(record.MatchesCurrentRoster(request.Id,
            request.SourceContractId, request.SourceRoomId, request.BuildingId,
            new[] { party[0], new RoomChangeRosterMember(
                party[1].TenantId, ContractTenantRole.PrimaryTenant) }));
    }

    [Fact]
    public void TRF28_Tamper_Of_Canonical_Payload_Is_Rejected()
    {
        var request = Request();
        var record = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, Roster(),
            Guid.NewGuid(), Now.AddSeconds(1));
        Assert.DoesNotContain("name", record.RosterJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", record.RosterJson,
            StringComparison.OrdinalIgnoreCase);
        // No setters: tamper is simulated by a corrupted persistence materializer.
        var field = typeof(RoomChangeTenantRosterSnapshotRecord).GetField(
            "<RosterSha256>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(record, new string('0', 64));
        Assert.Throws<InvalidDataException>(() => record.ReadVerifiedRoster());
    }
}
