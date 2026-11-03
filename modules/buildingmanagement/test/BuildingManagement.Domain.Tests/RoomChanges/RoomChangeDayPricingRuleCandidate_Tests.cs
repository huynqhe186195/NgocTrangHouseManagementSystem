using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeDayPricingRuleCandidate_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed record F(
        RoomChangeRequest Request, Contract Source,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        RoomChangeDailyRentQuote Quote);

    private static F Fixture(DateOnly? day = null,
        RoomChangeTransferDayChargeOwner owner =
            RoomChangeTransferDayChargeOwner.TargetRoom,
        RoomChangeRentRoundingRule rounding =
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
        bool accepted = true,
        RoomChangePricingTreatment price =
            RoomChangePricingTreatment.AgreedDayProration)
    {
        var date = day ?? new DateOnly(2026, 11, 15);
        var source = new Contract(Guid.NewGuid(), "TRF_SOURCE",
            Guid.NewGuid(), new DateTime(2026, 10, 1), null, 3_000_000m);
        source.Activate();
        var request = RoomChangeRequest.Submit(Guid.NewGuid(), source.Id,
            source.RoomId, Guid.NewGuid(), Guid.NewGuid(), date,
            "Owner reviewed room change day-proration policy", Guid.NewGuid(),
            Now, ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request,
            new[] { new RoomChangeRosterMember(
                Guid.NewGuid(), ContractTenantRole.PrimaryTenant) },
            Guid.NewGuid(), Now.AddSeconds(1));
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            price, RoomChangeDepositTreatment.CarryAllWithNoDifference,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-NEW-CONTRACT-REF-01", "SIGNED-PRORATE-REF-001",
            "SIGNED-DEPOSIT-REF-001", "SIGNED-ALL-TENANTS-REF-001",
            Guid.NewGuid(), Now.AddSeconds(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster,
            Guid.NewGuid(), Now.AddSeconds(3), accepted,
            "INDEPENDENT-ROOT-PRICE-REVIEW-001",
            "Independent decision tied to source contract");
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            date, 3_000_000m, 4_000_000m, owner, rounding,
            policy.PricingPolicyReference);
        return new F(request, source, roster, policy, review, quote);
    }

    private static RoomChangeDayPricingRuleCandidate Build(F f,
        RoomChangeDailyRentQuote? quote = null, Guid? preparer = null)
        => RoomChangeDayPricingRuleCandidate.Prepare(
            Guid.NewGuid(), f.Request, f.Source, f.Roster, f.Policy,
            f.Review, quote ?? f.Quote, preparer ?? Guid.NewGuid(),
            Now.AddSeconds(4));

    [Fact]
    public void TRF_RULE01_Approved_Day_Owner_Price_Snapshot_Verifies()
    {
        var f = Fixture();
        var rule = Build(f);
        rule.Verify(f.Request, f.Source, f.Roster, f.Policy, f.Review);
        Assert.Equal(f.Request.Id, rule.RequestId);
        Assert.Equal(14, rule.SourceChargeableDays);
        Assert.Equal(16, rule.TargetChargeableDays);
        Assert.Equal(1_400_000m, rule.ProposedSourceRentVnd);
        Assert.Equal(2_133_333m, rule.ProposedTargetRentVnd);
        Assert.Equal(64, rule.EvidenceSha256.Length);
        Assert.False(rule.CanExecuteRoomChange);
        Assert.False(rule.CanPostIssuedInvoiceAdjustment);
        Assert.False(rule.IsOwnerGlobalPricingApproval);
    }

    [Fact]
    public void TRF_RULE02_Explicit_Source_Owns_Transfer_Day()
    {
        var f = Fixture(owner: RoomChangeTransferDayChargeOwner.SourceRoom);
        var rule = Build(f);
        Assert.Equal(15, rule.SourceChargeableDays);
        Assert.Equal(15, rule.TargetChargeableDays);
        rule.Verify(f.Request, f.Source, f.Roster, f.Policy, f.Review);
    }

    [Fact]
    public void TRF_RULE03_Leap_Year_And_ThirtyOneDay_Denominator()
    {
        foreach (var date in new[] {
            new DateOnly(2028, 2, 29),
            new DateOnly(2026, 12, 15)
        })
        {
            var f = Fixture(date);
            var rule = Build(f);
            Assert.Equal(DateTime.DaysInMonth(date.Year, date.Month),
                rule.SourceChargeableDays + rule.TargetChargeableDays);
            rule.Verify(f.Request, f.Source, f.Roster, f.Policy, f.Review);
        }
    }

    [Fact]
    public void TRF_RULE04_Truncation_Requires_Explicit_Distinct_Policy()
    {
        var f = Fixture(rounding: RoomChangeRentRoundingRule.TruncateToWholeVnd);
        var rule = Build(f);
        Assert.Equal(RoomChangeRentRoundingRule.TruncateToWholeVnd,
            rule.Rounding);
        rule.Verify(f.Request, f.Source, f.Roster, f.Policy, f.Review);
    }

    [Fact]
    public void TRF_RULE05_Rejected_Review_Or_FullMonth_Choice_Is_Denied()
    {
        Assert.Throws<InvalidOperationException>(() => Build(Fixture(accepted: false)));
        Assert.Throws<InvalidOperationException>(() => Build(Fixture(price:
            RoomChangePricingTreatment.FullOldMonthNewNextMonth)));
    }

    [Fact]
    public void TRF_RULE06_Reviewer_Cannot_SelfPrepare_Same_Rule()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            Build(f, preparer: f.Review.ReviewedByUserId));
    }

    [Fact]
    public void TRF_RULE07_Forged_Foreign_Quote_And_Date_Are_Denied()
    {
        var f = Fixture();
        var wrong = f.Quote with { ProposedSourceRentVnd = 1m };
        Assert.Throws<InvalidOperationException>(() => Build(f, quote: wrong));
        var date = f.Quote with { TransferDate = new DateOnly(2026, 11, 16) };
        Assert.Throws<InvalidOperationException>(() => Build(f, quote: date));
        var policy = f.Quote with { SignedRuleReference = "FORGED-PRICING-REF-001" };
        Assert.Throws<InvalidOperationException>(() => Build(f, quote: policy));
    }

    [Fact]
    public void TRF_RULE08_Stored_Quote_Field_Tampering_Or_Foreign_Source_Detected()
    {
        var f = Fixture();
        var rule = Build(f);
        var property = typeof(RoomChangeDayPricingRuleCandidate).GetField(
            "<ProposedSourceRentVnd>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(property);
        property!.SetValue(rule, 1m);
        Assert.Throws<InvalidDataException>(() =>
            rule.Verify(f.Request, f.Source, f.Roster, f.Policy, f.Review));
        var clean = Build(f);
        var other = Fixture();
        Assert.Throws<InvalidDataException>(() =>
            clean.Verify(other.Request, other.Source, other.Roster,
                other.Policy, other.Review));
    }
}
