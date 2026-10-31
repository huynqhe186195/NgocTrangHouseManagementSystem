using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeCompletion_Tests
{
    private static readonly DateTime Created = new(
        2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Finished = new(
        2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static (RoomChangeRequest Request, Contract OldContract,
        Contract NewContract, RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeHandoverEvidenceRecord Handover,
        DepositSettlement Carry) Fixture(
            RoomChangePricingTreatment pricing =
                RoomChangePricingTreatment.FullOldMonthNewNextMonth,
            RoomChangeDepositTreatment deposit =
                RoomChangeDepositTreatment.CarryAllWithNoDifference)
    {
        var source = new Contract(Guid.NewGuid(),
            "ORIGINAL-"+Guid.NewGuid().ToString("N"), Guid.NewGuid(),
            new DateTime(2026, 9, 1), null, 3_000_000m);
        source.Activate();
        var request = RoomChangeRequest.Submit(Guid.NewGuid(), source.Id,
            source.RoomId, Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 10, 31),
            "Moving to a new room under a signed agreement",
            Guid.NewGuid(), Created, ContractStatus.Active,
            RoomStatus.Occupied, RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, new[] {
                new RoomChangeRosterMember(Guid.NewGuid(),
                    ContractTenantRole.PrimaryTenant)
            }, Guid.NewGuid(), Created.AddMinutes(1));
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            pricing, deposit, RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-NEW-CONTRACT-2026-001",
            "SIGNED-OLD-FULL-MONTH-2026-001",
            "SIGNED-NO-CASH-DIFFERENCE-2026-001",
            "SIGNED-ALL-TENANTS-2026-001",
            Guid.NewGuid(), Created.AddMinutes(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster, Guid.NewGuid(),
            Created.AddMinutes(3), true, "SIGNED-OWNER-REVIEW-2026-001",
            "Owner confirms unchanged deposit and whole old month");
        var e = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Electricity, "E-HANDOVER", 0m, new DateTime(2026, 9, 1));
        var w = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Water, "W-HANDOVER", 0m, new DateTime(2026, 9, 1));
        var er = new MeterReading(Guid.NewGuid(), e.Id, 2026, 10,
            100m, 170m, new DateTime(2026, 10, 31));
        var wr = new MeterReading(Guid.NewGuid(), w.Id, 2026, 10,
            30m, 42m, new DateTime(2026, 10, 31));
        var handover = RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), request, e, er, w, wr,
            "SIGNED-INSPECTION-2026-001", "SIGNED-HANDOVER-2026-001",
            Guid.NewGuid(), Finished.AddMinutes(-2));
        var next = new Contract(Guid.NewGuid(),
            "TRANSFER-"+Guid.NewGuid().ToString("N"), request.TargetRoomId,
            new DateTime(2026, 11, 1), null, source.MonthlyRent,
            renewedFromContractId: source.Id);
        next.Activate();
        var carry = new DepositSettlement(Guid.NewGuid(),
            DepositSettlementType.RoomChangeCarryForward,
            source.DepositAmount, sourceContractId: source.Id,
            targetContractId: next.Id);
        carry.Complete(Finished.AddMinutes(-1));
        source.End(Finished, ContractEndReason.RoomChanged);
        return (request, source, next, policy, review, roster, handover, carry);
    }

    private static RoomChangeCompletionRecord Complete(
        (RoomChangeRequest Request, Contract OldContract,
        Contract NewContract, RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeHandoverEvidenceRecord Handover,
        DepositSettlement Carry) f)
        => RoomChangeCompletionRecord.Complete(
            Guid.NewGuid(), f.Request, f.OldContract, f.NewContract,
            f.Policy, f.Review, f.Roster, f.Handover, f.Carry,
            RoomStatus.Available,
            "SIGNED-CLEAN-ROOM-INSPECTION-001",
            "SIGNED-ROOM-CHANGE-COMPLETION-001",
            Guid.NewGuid(), Finished);

    [Fact]
    public void TRF48_Completed_OneShot_Path_Protects_Linked_Contracts_And_Cash()
    {
        var f = Fixture();
        var receipt = Complete(f);
        receipt.VerifyFingerprint();
        Assert.Equal(f.OldContract.Id, receipt.SourceContractId);
        Assert.Equal(f.NewContract.Id, receipt.TargetContractId);
        Assert.Equal(f.Carry.Id, receipt.CarryForwardSettlementId);
        Assert.Equal(3_000_000m, receipt.CarriedDepositAmount);
        Assert.Equal(64, receipt.EvidenceSha256.Length);
        Assert.Equal(RoomStatus.Available, receipt.SourceRoomDisposition);
    }

    [Theory]
    [InlineData(RoomChangePricingTreatment.AgreedDayProration,
        RoomChangeDepositTreatment.CarryAllWithNoDifference)]
    [InlineData(RoomChangePricingTreatment.FullOldMonthNewNextMonth,
        RoomChangeDepositTreatment.CarryWithVerifiedTopUp)]
    [InlineData(RoomChangePricingTreatment.FullOldMonthNewNextMonth,
        RoomChangeDepositTreatment.PartialCarryWithRefund)]
    public void TRF49_Undefined_Pricing_Or_Extra_Cash_Lanes_Fail_Closed(
        RoomChangePricingTreatment pricing, RoomChangeDepositTreatment deposit)
    {
        var f = Fixture(pricing, deposit);
        Assert.Throws<InvalidOperationException>(() => Complete(f));
    }

    [Fact]
    public void TRF50_Immutable_Completion_Fingerprint_Detects_Tampering()
    {
        var f = Fixture();
        var receipt = Complete(f);
        var field = typeof(RoomChangeCompletionRecord).GetField(
            "<CarriedDepositAmount>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(receipt, 1m);
        Assert.Throws<InvalidDataException>(() => receipt.VerifyFingerprint());
    }

    [Fact]
    public void TRF51_Only_Terminal_Review_With_Audit_Reference_Can_Close_Request()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            f.Request.Complete(Guid.NewGuid(), Finished, "short"));
        var actor = Guid.NewGuid();
        f.Request.Complete(actor, Finished,
            "SIGNED-ROOM-CHANGE-COMPLETION-001");
        Assert.Equal(RoomChangeRequestStatus.Completed, f.Request.Status);
        Assert.Equal(actor, f.Request.ReviewedByUserId);
        Assert.Throws<InvalidOperationException>(() =>
            f.Request.Complete(actor, Finished.AddSeconds(1),
                "SIGNED-REPLAY-TRANSFER-001"));
    }
}
