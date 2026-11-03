using System;
using System.IO;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeFinalIndependentSignoffs_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Facts(
        RoomChangeRequest Request, Contract Source,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        RoomChangeDayPricingRuleCandidate Rule,
        RoomChangeHandoverEvidenceRecord Physical,
        Guid PrimaryTenantId);

    private static Facts Create()
    {
        var source = new Contract(Guid.NewGuid(), "SIGNED-2H-SOURCE-001",
            Guid.NewGuid(), new DateTime(2026, 10, 1), null, 3_000_000m);
        source.Activate();
        var request = RoomChangeRequest.Submit(Guid.NewGuid(), source.Id,
            source.RoomId, Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 11, 30),
            "Reviewed contract change with exact signed day-proration",
            Guid.NewGuid(), Now, ContractStatus.Active,
            RoomStatus.Occupied, RoomStatus.Available);
        var primary = Guid.NewGuid();
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, new[] {
                new RoomChangeRosterMember(primary, ContractTenantRole.PrimaryTenant),
                new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.Member)
            }, Guid.NewGuid(), Now.AddSeconds(1));
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.AgreedDayProration,
            RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-NEW-CONTRACT-TRANSFER-2026",
            "SIGNED-CHARGE-DAY-AND-ROUNDING-2026",
            "SIGNED-TARGET-DEPOSIT-REQUIREMENT-2026",
            "SIGNED-ALL-TENANTS-TRANSFER-2026",
            Guid.NewGuid(), Now.AddSeconds(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster,
            Guid.NewGuid(), Now.AddSeconds(3), true,
            "INDEPENDENT-OWNER-OI07-REVIEW-2026",
            "Owner independently reviewed the original transfer policy");
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            request.RequestedDate, 3_000_000m, 4_000_000m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            policy.PricingPolicyReference);
        var rule = RoomChangeDayPricingRuleCandidate.Prepare(
            Guid.NewGuid(), request, source, roster, policy, review,
            quote, Guid.NewGuid(), Now.AddSeconds(4));

        var electricity = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Electricity, "H6-SIGN-E-METER-001", 0m,
            new DateTime(2026, 10, 1));
        var water = new UtilityMeter(Guid.NewGuid(), source.RoomId,
            UtilityType.Water, "H6-SIGN-W-METER-001", 0m,
            new DateTime(2026, 10, 1));
        var e = new MeterReading(Guid.NewGuid(), electricity.Id,
            2026, 11, 10m, 80m, new DateTime(2026, 11, 30));
        var w = new MeterReading(Guid.NewGuid(), water.Id,
            2026, 11, 5m, 35m, new DateTime(2026, 11, 30));
        var physical = RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), request, electricity, e, water, w,
            "SIGNED-ASSET-INSPECTION-H6-2026",
            "SIGNED-METER-HANDOVER-H6-2026",
            Guid.NewGuid(), Now.AddSeconds(5));
        return new Facts(request, source, roster, policy, review,
            rule, physical, primary);
    }

    private static RoomChangeDayPricingOwnerSignoff OwnerSign(Facts f,
        Guid? owner = null, DateTime? at = null, string? reference = null)
        => RoomChangeDayPricingOwnerSignoff.Sign(
            Guid.NewGuid(), f.Rule, f.Request, f.Source,
            f.Roster, f.Policy, f.Review,
            owner ?? f.Review.ReviewedByUserId,
            at ?? Now.AddSeconds(7),
            reference ?? "OWNER-SIGNED-EXACT-NOVEMBER-D01-D02-CHOICE-2026");

    private static RoomChangeHandoverThreePartySignoff HandoverSign(
        Facts f, Guid? owner = null, Guid? staff = null,
        Guid? primary = null, DateTime? at = null,
        string? tenantRef = null)
        => RoomChangeHandoverThreePartySignoff.Sign(
            Guid.NewGuid(), f.Request, f.Roster, f.Physical,
            owner ?? f.Review.ReviewedByUserId,
            staff ?? f.Physical.AttestedByUserId,
            primary ?? f.PrimaryTenantId,
            "OWNER-SIGNED-TRANSFER-CHECKLIST-2026",
            "STAFF-SIGNED-METER-AND-ASSET-2026",
            tenantRef ?? "TENANT-SIGNED-ROOM-HANDOVER-2026",
            "SIGNED-DOCUMENTED-ASSET-DEFECT-REVIEW-2026",
            at ?? Now.AddSeconds(8));

    [Fact]
    public void TRF_FINAL01_Owner_Signs_Exact_Previously_Reviewed_Pricing_Snapshot()
    {
        var f = Create();
        var signed = OwnerSign(f);
        signed.Verify(f.Rule, f.Request, f.Source, f.Roster, f.Policy, f.Review);
        Assert.Equal(f.Rule.EvidenceSha256, signed.PricingRuleSha256);
        Assert.Equal(64, signed.EvidenceSha256.Length);
        Assert.False(signed.CanPostInvoice);
        Assert.False(signed.CanExecuteTransfer);
    }

    [Fact]
    public void TRF_FINAL02_NonOwner_Or_Pricing_Preparer_Cannot_Sign()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            OwnerSign(f, owner: Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() =>
            OwnerSign(f, owner: f.Rule.PreparedByUserId));
    }

    [Fact]
    public void TRF_FINAL03_Stale_Timestamp_Or_Missing_Signed_Choice_Denied()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            OwnerSign(f, at: Now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            OwnerSign(f, reference: "SHORT"));
    }

    [Fact]
    public void TRF_FINAL04_Another_Room_Or_Roster_Cannot_Use_Same_Pricing_Signature()
    {
        var f = Create();
        var other = Create();
        var signed = OwnerSign(f);
        Assert.ThrowsAny<Exception>(() =>
            signed.Verify(other.Rule, other.Request,
                other.Source, other.Roster, other.Policy, other.Review));
    }

    [Fact]
    public void TRF_FINAL05_H6_Handover_Has_Three_Distinct_Signed_Roles()
    {
        var f = Create();
        var signed = HandoverSign(f);
        signed.Verify(f.Request, f.Roster, f.Physical);
        Assert.Equal(f.PrimaryTenantId, signed.TenantRepresentativeId);
        Assert.Equal(f.Physical.EvidenceSha256, signed.PhysicalHandoverSha256);
        Assert.Equal(f.Roster.RosterSha256, signed.RosterSha256);
        Assert.Equal(64, signed.EvidenceSha256.Length);
        Assert.False(signed.IsCryptographicallyAuthenticatedSignature);
        Assert.False(signed.CanExecuteTransfer);
    }

    [Fact]
    public void TRF_FINAL06_Another_Staff_Or_NonPrimary_Tenant_Refused()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            HandoverSign(f, staff: Guid.NewGuid()));
        var nonprimary = f.Roster.ReadVerifiedRoster().Single(
            x => x.Role == ContractTenantRole.Member).TenantId;
        Assert.Throws<InvalidOperationException>(() =>
            HandoverSign(f, primary: nonprimary));
    }

    [Fact]
    public void TRF_FINAL07_Duplicate_Signature_Refs_Or_Old_Time_Refused()
    {
        var f = Create();
        Assert.Throws<InvalidOperationException>(() =>
            HandoverSign(f, at: Now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            HandoverSign(f, tenantRef: "OWNER-SIGNED-TRANSFER-CHECKLIST-2026"));
    }

    [Fact]
    public void TRF_FINAL08_Foreign_Request_Roster_Or_Physical_Proof_Detected()
    {
        var f = Create();
        var other = Create();
        var signed = HandoverSign(f);
        Assert.ThrowsAny<Exception>(() =>
            signed.Verify(other.Request, other.Roster, other.Physical));
    }
}
