using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangePolicyReview_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static (RoomChangeRequest Request,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy, Guid Proposer) Fixture()
    {
        var req = RoomChangeRequest.Submit(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 12, 1),
            "Signed request for transfer", Guid.NewGuid(), Now,
            ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), req, new[] {
                new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.PrimaryTenant)
            }, Guid.NewGuid(), Now.AddSeconds(1));
        var proposer = Guid.NewGuid();
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), req, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.FullOldMonthNewNextMonth,
            RoomChangeDepositTreatment.CarryAllWithNoDifference,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-CONTRACT-POLICY-001", "SIGNED-PRICING-POLICY-001",
            "SIGNED-DEPOSIT-POLICY-001", "SIGNED-TENANT-POLICY-001",
            proposer, Now.AddSeconds(2));
        return (req, roster, policy, proposer);
    }

    [Fact]
    public void TRF35_Independent_Acceptance_Does_Not_Execute_Transfer()
    {
        var f = Fixture();
        var approved = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), f.Policy, f.Request, f.Roster,
            Guid.NewGuid(), Now.AddSeconds(3), true,
            "SIGNED-ROOT-REVIEW-001", "Explicit plan approved for further checks");
        approved.Verify(f.Policy, f.Request, f.Roster);
        Assert.True(approved.Accepted);
        Assert.False(approved.IsTransferExecuted);
    }

    [Fact]
    public void TRF36_Proposer_Cannot_Review_Own_Policy()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeBusinessPolicyReviewRecord.Review(
                Guid.NewGuid(), f.Policy, f.Request, f.Roster, f.Proposer,
                Now.AddSeconds(3), true, "SIGNED-REVIEW-SELF",
                "Self approval is not independent"));
    }

    [Fact]
    public void TRF37_Rejection_Is_Durable_NonExecutable_And_Documented()
    {
        var f = Fixture();
        var rejected = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), f.Policy, f.Request, f.Roster,
            Guid.NewGuid(), Now.AddSeconds(3), false,
            "SIGNED-REJECTION-001", "Insufficient consent evidence");
        rejected.Verify(f.Policy, f.Request, f.Roster);
        Assert.False(rejected.Accepted);
        Assert.False(rejected.IsTransferExecuted);
    }

    [Fact]
    public void TRF38_Review_Rejects_Mismatched_Policy_Or_Evidence()
    {
        var f = Fixture();
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), f.Policy, f.Request, f.Roster,
            Guid.NewGuid(), Now.AddSeconds(3), true,
            "SIGNED-REVIEW-ROOT-001", "Root confirms policy reference");
        var badPolicy = typeof(RoomChangeBusinessPolicyReviewRecord).GetField(
            "<ProposalSha256>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(badPolicy);
        badPolicy!.SetValue(review, new string('f', 64));
        Assert.Throws<InvalidDataException>(() =>
            review.Verify(f.Policy, f.Request, f.Roster));
    }
}
