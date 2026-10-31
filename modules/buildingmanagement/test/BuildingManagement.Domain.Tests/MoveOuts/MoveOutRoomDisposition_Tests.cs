using System;
using System.IO;
using System.Reflection;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.MoveOuts;

/// <summary>
/// Phase 2G.5: room release must be driven by proven future commitments
/// and signed repair evidence. No reservation is ever auto-cancelled.
/// </summary>
public sealed class MoveOutRoomDisposition_Tests
{
    private static readonly DateTime Completed =
        new(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ClosingContract = Guid.NewGuid();

    [Fact]
    public void CMOUT51_Clean_Room_Without_Future_Booking_Becomes_Available()
    {
        var d = Create();
        Assert.Equal(RoomStatus.Available, d.ResultRoomStatus);
        Assert.Null(d.FutureReservationId);
        Assert.False(d.RequiresMaintenance);
        Assert.False(d.IsDepositRefundInstruction);
        d.VerifyStoredDisposition();
    }

    [Fact]
    public void CMOUT52_Confirmed_Reservation_Retains_Room_As_Reserved()
    {
        var futureId = Guid.NewGuid();
        var d = Create(reservations: new[] {
            new MoveOutRoomReservationFact(futureId, RoomReservationStatus.Reserved)
        });
        Assert.Equal(RoomStatus.Reserved, d.ResultRoomStatus);
        Assert.Equal(futureId, d.FutureReservationId);
        Assert.Null(d.FutureSignedContractId);
        d.VerifyStoredDisposition();
    }

    [Fact]
    public void CMOUT53_Signed_Next_Contract_Retains_Room_As_Reserved()
    {
        var nextId = Guid.NewGuid();
        var d = Create(contracts: new[] {
            new MoveOutRoomSignedContractFact(nextId, ContractStatus.Signed)
        });
        Assert.Equal(RoomStatus.Reserved, d.ResultRoomStatus);
        Assert.Equal(nextId, d.FutureSignedContractId);
        Assert.Null(d.FutureReservationId);
    }

    [Fact]
    public void CMOUT54_Pending_Unpaid_Reservation_Is_Not_Confirmed_Commitment()
    {
        var d = Create(reservations: new[] {
            new MoveOutRoomReservationFact(Guid.NewGuid(),
                RoomReservationStatus.PendingPayment)
        });
        Assert.Equal(RoomStatus.Available, d.ResultRoomStatus);
    }

    [Fact]
    public void CMOUT55_Approved_Damage_Requires_Maintenance_Evidence()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            damage: 250000m));
        var d = Create(damage: 250000m, maintenanceReason:
            "Broken bathroom door requires repair",
            maintenanceProof: "SIGNED-INSPECTION-PHOTOS-204");
        Assert.Equal(RoomStatus.Maintenance, d.ResultRoomStatus);
        Assert.True(d.RequiresMaintenance);
        d.VerifyStoredDisposition();
    }

    [Fact]
    public void CMOUT56_Signed_Repair_Decision_Without_Charge_Still_Maintenance()
    {
        var d = Create(maintenance: true,
            maintenanceReason: "Electric wiring requires safety inspection",
            maintenanceProof: "SIGNED-MAINTENANCE-REPORT-204");
        Assert.Equal(0, d.FutureReservationId.GetValueOrDefault().CompareTo(Guid.Empty));
        Assert.Equal(RoomStatus.Maintenance, d.ResultRoomStatus);
    }

    [Fact]
    public void CMOUT57_Damaged_Room_With_Future_Booking_Is_Hold_Not_AutoCancel()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            damage: 500000m, maintenanceReason: "Broken window panel",
            maintenanceProof: "SIGNED-PHOTO-WINDOW-204",
            reservations: new[] {
                new MoveOutRoomReservationFact(Guid.NewGuid(),
                    RoomReservationStatus.Reserved)
            }));
    }

    [Fact]
    public void CMOUT58_Two_Committed_Future_Occupancies_Are_Conflict()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            reservations: new[] {
                new MoveOutRoomReservationFact(Guid.NewGuid(), RoomReservationStatus.Reserved),
                new MoveOutRoomReservationFact(Guid.NewGuid(), RoomReservationStatus.Reserved)
            }));
        Assert.Throws<InvalidOperationException>(() => Create(
            reservations: new[] {
                new MoveOutRoomReservationFact(Guid.NewGuid(), RoomReservationStatus.Reserved)
            }, contracts: new[] {
                new MoveOutRoomSignedContractFact(Guid.NewGuid(), ContractStatus.Signed)
            }));
    }

    [Fact]
    public void CMOUT59_Unsigned_Repair_Proof_Or_Unexpected_CleanRoom_Damage_Is_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            maintenance: true, maintenanceReason: "Broken window panel"));
        Assert.Throws<InvalidOperationException>(() => Create(
            maintenanceProof: "SIGNED-MAINTENANCE-REPORT-204"));
    }

    [Fact]
    public void CMOUT60_Proof_And_Room_Status_Are_Immutable_After_Approval()
    {
        var d = Create();
        Set(d, "DispositionJson", d.DispositionJson + " ");
        Assert.Throws<InvalidDataException>(d.VerifyStoredDisposition);
        var clean = Create();
        Set(clean, "ResultRoomStatus", RoomStatus.Reserved);
        Assert.Throws<InvalidDataException>(clean.VerifyStoredDisposition);
    }

    [Fact]
    public void CMOUT61_Closeout_Timestamp_Must_Be_Utc_And_Room_Occupied()
    {
        Assert.Throws<InvalidOperationException>(() => Create(
            time: DateTime.SpecifyKind(Completed, DateTimeKind.Unspecified)));
        Assert.Throws<InvalidOperationException>(() => Create(
            status: RoomStatus.Available));
    }

    private static void Set(MoveOutRoomDispositionRecord d,
        string property, object value)
    {
        typeof(MoveOutRoomDispositionRecord)
            .GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(d, value);
    }

    private static MoveOutRoomDispositionRecord Create(
        decimal damage = 0m, bool maintenance = false,
        string? maintenanceReason = null, string? maintenanceProof = null,
        MoveOutRoomReservationFact[]? reservations = null,
        MoveOutRoomSignedContractFact[]? contracts = null,
        DateTime? time = null, RoomStatus status = RoomStatus.Occupied)
        => MoveOutRoomDispositionRecord.Complete(
            Guid.NewGuid(), ClosingContract, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            time ?? Completed, status, damage,
            reservations ?? Array.Empty<MoveOutRoomReservationFact>(),
            contracts ?? Array.Empty<MoveOutRoomSignedContractFact>(),
            maintenance, maintenanceReason, maintenanceProof,
            "SIGNED-HANDOVER-REPORT-204");
}
