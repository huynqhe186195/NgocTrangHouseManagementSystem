using System;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

/// <summary>H6 is defense-in-depth at the existing atomic transaction.</summary>
public sealed partial class RoomChangeCompletion_Tests
{
    private static RoomChangeReducedHandoverCompletionFacts ValidH6()
    {
        var f = Fixture();
        return new RoomChangeReducedHandoverCompletionFacts(
            f.Request, f.Roster, f.Policy, f.Review, f.Handover,
            RoomStatus.Available, false,
            "SIGNED-CLEAN-ROOM-INSPECTION-001",
            "SIGNED-ROOM-CHANGE-COMPLETION-001",
            Guid.NewGuid(), Finished,
            true, true, true, true);
    }

    [Fact]
    public void TRF_HAND01_Signed_Minimal_Source_Package_Accepted_Only_After_Server_Checks()
    {
        var facts = ValidH6();
        RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(facts);
        Assert.NotEqual(Guid.Empty, facts.Request.Id);
        Assert.Equal(facts.Request.Id, facts.Handover.RequestId);
    }

    [Fact]
    public void TRF_HAND02_Missing_Original_Electricity_Or_Water_Verification_Denied()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { ServerVerifiedOriginalElectricityWaterReadings = false }));
    }

    [Fact]
    public void TRF_HAND03_Tenant_Roster_Drift_Denies_Source_To_Target_Transfer()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { ServerVerifiedExactFullTenantRoster = false }));
        var other = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { Roster = other.Roster }));
    }

    [Fact]
    public void TRF_HAND04_Unsettled_Source_Invoice_Debt_Blocks_Handover()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { ServerVerifiedAllOfficialSourceReceivablesSettled = false }));
    }

    [Fact]
    public void TRF_HAND05_Missing_Actual_Deposit_Cash_Proof_Blocks_Handover()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { ServerVerifiedActualCashBackedHeldDeposit = false }));
    }

    [Fact]
    public void TRF_HAND06_Confirmed_Next_Booking_Must_Remain_Reserved()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { HasConfirmedFutureSourceReservation = true }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { SourceRoomDisposition = RoomStatus.Reserved }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with {
                    HasConfirmedFutureSourceReservation = true,
                    SourceRoomDisposition = RoomStatus.Maintenance
                }));
        RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
            facts with {
                HasConfirmedFutureSourceReservation = true,
                SourceRoomDisposition = RoomStatus.Reserved
            });
    }

    [Fact]
    public void TRF_HAND07_Unsigned_Disposition_Or_Completion_Is_Not_A_Checklist()
    {
        var facts = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { SourceDispositionEvidenceReference = "short" }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { CompletionReference = null }));
    }

    [Fact]
    public void TRF_HAND08_Foreign_Request_Actor_Or_Backdated_Completion_Is_Denied()
    {
        var facts = ValidH6();
        var other = ValidH6();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { Request = other.Request }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { CompletedByUserId = Guid.Empty }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeReducedHandoverChecklistGate.RequireForAtomicCompletion(
                facts with { CompletedAtUtc = Finished.AddHours(-4) }));
    }
}
