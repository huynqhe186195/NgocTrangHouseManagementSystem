using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeBusinessPolicy_Tests
{
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static (RoomChangeRequest Request, RoomChangeTenantRosterSnapshotRecord Roster)
        Fixture()
    {
        var request = RoomChangeRequest.Submit(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 12, 31), "Written request for a room change",
            Guid.NewGuid(), Now, ContractStatus.Active, RoomStatus.Occupied,
            RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request,
            new[] { new RoomChangeRosterMember(
                Guid.NewGuid(), ContractTenantRole.PrimaryTenant) },
            Guid.NewGuid(), Now.AddSeconds(1));
        return (request, roster);
    }

    private static RoomChangeBusinessPolicyRecord Plan(
        RoomChangeRequest req, RoomChangeTenantRosterSnapshotRecord roster,
        Guid? actor = null, RoomChangeDepositTreatment deposit =
            RoomChangeDepositTreatment.CarryAllWithNoDifference)
        => RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), req, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.FullOldMonthNewNextMonth,
            deposit, RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-CONTRACT-TREATMENT-REF-001",
            "SIGNED-PRICING-DECISION-REF-001",
            "SIGNED-DEPOSIT-POLICY-REF-001",
            "SIGNED-TENANT-CONSENT-REF-001",
            actor ?? Guid.NewGuid(), Now.AddSeconds(3));

    [Fact]
    public void TRF30_Canonical_Choice_And_Roster_Fingerprint_Verify()
    {
        var (req, roster) = Fixture();
        var plan = Plan(req, roster);
        plan.Verify(req, roster);
        Assert.False(plan.IsExecuted);
        Assert.False(plan.IsIndependentlyApproved);
        Assert.Equal(64, plan.EvidenceSha256.Length);
        Assert.Equal(roster.RosterSha256, plan.RosterSha256);
    }

    [Fact]
    public void TRF31_Unknown_Strategy_Or_Unreferenced_Decision_Is_Denied()
    {
        var (req, roster) = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeBusinessPolicyRecord.Propose(Guid.NewGuid(), req, roster,
                (RoomChangeContractTreatment)99,
                RoomChangePricingTreatment.AgreedDayProration,
                RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
                RoomChangeTenantTreatment.ConsentingSubset,
                "SIGNED-CONTRACT-POLICY-001", "SIGNED-PRICING-POLICY-001",
                "SIGNED-DEPOSIT-POLICY-001", "SIGNED-CONSENT-POLICY-001",
                Guid.NewGuid(), Now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeBusinessPolicyRecord.Propose(Guid.NewGuid(), req, roster,
                RoomChangeContractTreatment.NewContract,
                RoomChangePricingTreatment.AgreedDayProration,
                RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
                RoomChangeTenantTreatment.ConsentingSubset,
                "SIGNED-CONTRACT-POLICY-001", "", "SIGNED-DEPOSIT-POLICY-001",
                "SIGNED-CONSENT-POLICY-001", Guid.NewGuid(), Now.AddSeconds(2)));
    }

    [Fact]
    public void TRF32_Stale_Roster_And_Terminal_Request_Are_Denied()
    {
        var (request, roster) = Fixture();
        var other = Fixture();
        Assert.Throws<InvalidOperationException>(() => Plan(request, other.Roster));
        request.Cancel(Guid.NewGuid(), Now.AddSeconds(4));
        Assert.Throws<InvalidOperationException>(() => Plan(request, roster));
    }

    [Fact]
    public void TRF33_Forged_Stored_Source_Is_Detected()
    {
        var (req, roster) = Fixture();
        var plan = Plan(req, roster);
        var prop = typeof(RoomChangeBusinessPolicyRecord).GetField(
            "<DepositPolicyReference>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(prop);
        prop!.SetValue(plan, "TAMPERED-DEPOSIT-CHOICE");
        Assert.Throws<InvalidDataException>(() => plan.Verify(req, roster));
    }

    [Fact]
    public void TRF34_No_Implicit_Transfer_Or_Approval_For_Any_Deposit_Option()
    {
        var (req, roster) = Fixture();
        foreach (var deposit in new[]
            {
                RoomChangeDepositTreatment.CarryAllWithNoDifference,
                RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
                RoomChangeDepositTreatment.PartialCarryWithRefund
            })
        {
            var plan = Plan(req, roster, deposit: deposit);
            Assert.False(plan.IsExecuted);
            Assert.False(plan.IsIndependentlyApproved);
        }
    }
}
