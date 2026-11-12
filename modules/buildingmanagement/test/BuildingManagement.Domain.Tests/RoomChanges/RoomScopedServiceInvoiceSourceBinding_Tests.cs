using System;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.RoomChanges;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomScopedServiceInvoiceSourceBinding_Tests
{
    private static readonly DateTime ReviewTime =
        new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    private static (
        MonthlyInvoiceCandidateLedgerRecord Candidate,
        RoomScopedServiceTariffVersion Tariff,
        RoomScopedServiceChargePreview Quote) Fixture(
        bool prorated = false, bool noFee = false)
    {
        var room = Guid.NewGuid();
        var tariff = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), room, Guid.NewGuid(), 1,
            150_000m, RoomScopedServiceBillingUnit.PerPerson,
            RoomScopedServiceChargingRule.CalendarDayProration,
            new DateOnly(2026, 11, 1), null,
            Guid.NewGuid(),
            new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc),
            "OWNER-SIGNED-ROOM301-INTERNET-SERVICE-RATE-001");
        var quote = tariff.PreviewCharge(
            new DateOnly(2026, 11, 1),
            prorated ? new DateOnly(2026, 11, 14) :
                new DateOnly(2026, 11, 30), 2);
        var meterKind = Enum.GetValues<MeterUsageSourceType>().First();
        var power = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Electricity, Guid.NewGuid(), 10m, 3500m,
            35_000m, new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 11, 4), 100m, 110m) });
        var water = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Water, Guid.NewGuid(), 1m, 15000m,
            15_000m, new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 11, 4), 55m, 56m) });
        var fee = new FinalInvoiceAuxiliaryChargeProposal(
            FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET",
            prorated ? 1m : 2m,
            prorated ? quote.ProposedChargeVnd : tariff.UnitPriceVnd,
            quote.ProposedChargeVnd, tariff.SignedPolicyReference,
            "Independently approved internet line for source room");
        var monthly = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), room, Guid.NewGuid(),
            2026, 11, Guid.NewGuid(), ReviewTime,
            new DateOnly(2026, 11, 20), "OWNER-APPROVED-DUE-DATE-POLICY-001",
            3_000_000m, 35_000m, 15_000m,
            noFee ? AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges :
                AuxiliaryFeeAssessmentDecision.ItemizedCharges,
            "OWNER-APPROVED-PER-ROOM-SERVICE-POLICY-001",
            noFee ? Array.Empty<FinalInvoiceAuxiliaryChargeProposal>() :
                new[] { fee },
            noFee ? 0m : fee.Amount,
            3_050_000m + (noFee ? 0m : fee.Amount),
            new[] { power, water });
        return (MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), monthly), tariff, quote);
    }

    private static RoomScopedServiceInvoiceSourceBinding Bind(
        MonthlyInvoiceCandidateLedgerRecord c,
        RoomScopedServiceTariffVersion t,
        RoomScopedServiceChargePreview q,
        string service = "INTERNET")
        => RoomScopedServiceInvoiceSourceBinding.Freeze(
            c, t, q, service, "VERIFIED-OCCUPANTS-AND-SERVICE-USAGE-001");

    [Fact]
    public void TRF_BILLSVC01_Binds_Exactly_Original_PerPerson_150k_Rate()
    {
        var (c,t,q) = Fixture();
        var binding = Bind(c,t,q);
        Assert.Equal(300_000m,binding.FrozenAuxiliaryLineAmountVnd);
        Assert.Equal(150_000m,binding.OriginalFullPeriodUnitPriceVnd);
        Assert.Equal(t.Id,binding.OriginalTariffVersionId);
        Assert.Equal(c.Id,binding.MonthlyCandidateId);
        Assert.Equal(c.FrozenEvidenceSha256,binding.OriginalMonthlyCandidateSha256);
        binding.VerifySourceFingerprint();
        Assert.False(binding.IsOfficialIssuedInvoice);
        Assert.False(binding.CanPostMoney);
    }

    [Fact]
    public void TRF_BILLSVC02_Cannot_Bind_Price_From_Another_Room()
    {
        var (c,t,q) = Fixture();
        var foreign = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(),Guid.NewGuid(),t.ServiceId,1,
            t.UnitPriceVnd,t.BillingUnit,t.ChargingRule,t.EffectiveFrom,
            null,t.ApprovedByUserId,t.ApprovedAtUtc,t.SignedPolicyReference);
        var other = foreign.PreviewCharge(
            q.BilledFromVietnam,q.BilledThroughVietnam,2);
        Assert.Throws<InvalidOperationException>(()=>Bind(c,foreign,other));
    }

    [Fact]
    public void TRF_BILLSVC03_Original_Tariff_Survives_Subsequent_Room_Rate_Edit()
    {
        var (c,t,q) = Fixture();
        var binding = Bind(c,t,q);
        var edited = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(),t.RoomId,t.ServiceId,2,200_000m,t.BillingUnit,
            t.ChargingRule,new DateOnly(2026,12,1),null,
            t.ApprovedByUserId,ReviewTime.AddDays(5),
            "OWNER-SIGNED-ROOM301-INTERNET-SERVICE-RATE-002");
        Assert.NotEqual(edited.EvidenceSha256,binding.OriginalTariffSha256);
        Assert.Equal(t.EvidenceSha256,binding.OriginalTariffSha256);
        binding.VerifySourceFingerprint();
    }

    [Fact]
    public void TRF_BILLSVC04_Prorated_14Day_Line_Binds_Aggregated_Charge()
    {
        var (c,t,q)=Fixture(prorated:true);
        var binding=Bind(c,t,q);
        Assert.Equal(new DateOnly(2026,11,14),
            binding.ServiceThroughVietnam);
        Assert.Equal(140_000m,binding.FrozenAuxiliaryLineAmountVnd);
        Assert.Equal(150_000m,binding.OriginalFullPeriodUnitPriceVnd);
        Assert.Equal(2,binding.VerifiedOccupantCount);
    }

    [Fact]
    public void TRF_BILLSVC05_Wrong_Service_Code_Fails_Closed()
    {
        var (c,t,q)=Fixture();
        Assert.Throws<InvalidOperationException>(()=>
            Bind(c,t,q,"PARKING"));
    }

    [Fact]
    public void TRF_BILLSVC06_Tampered_Rate_Or_Quote_Fails_Closed()
    {
        var (c,t,q)=Fixture();
        Assert.Throws<InvalidOperationException>(()=>
            Bind(c,t with { UnitPriceVnd=1m },q));
        Assert.Throws<InvalidOperationException>(()=>
            Bind(c,t,q with { ProposedChargeVnd=1m }));
    }

    [Fact]
    public void TRF_BILLSVC07_Forged_Candidate_Total_Fails_Closed()
    {
        var (c,t,q)=Fixture();
        typeof(MonthlyInvoiceCandidateLedgerRecord)
            .GetProperty(nameof(MonthlyInvoiceCandidateLedgerRecord.AuxiliaryAmount))!
            .SetValue(c,1m);
        Assert.Throws<System.IO.InvalidDataException>(()=>Bind(c,t,q));
    }

    [Fact]
    public void TRF_BILLSVC08_Explicit_NoFee_Decision_Cannot_Bind_Service_Charge()
    {
        var (c,t,q)=Fixture(noFee:true);
        Assert.Throws<InvalidOperationException>(()=>Bind(c,t,q));
    }

    [Fact]
    public void TRF_BILLSVC09_Mismatched_Month_And_Absent_Occupancy_Proof_Denied()
    {
        var (c,t,q)=Fixture();
        Assert.Throws<InvalidOperationException>(()=>
            Bind(c,t,q with {
                BilledFromVietnam=new DateOnly(2026,10,1)
            }));
        Assert.Throws<InvalidOperationException>(()=>
            RoomScopedServiceInvoiceSourceBinding.Freeze(c,t,q,"INTERNET",null));
    }

    [Fact]
    public void TRF_BILLSVC10_Evidence_Sha_Detects_Edited_Snapshot()
    {
        var (c,t,q)=Fixture();
        var binding=Bind(c,t,q);
        Assert.Throws<System.IO.InvalidDataException>(()=>
            (binding with { FrozenAuxiliaryLineAmountVnd=1m })
                .VerifySourceFingerprint());
        Assert.Throws<System.IO.InvalidDataException>(()=>
            (binding with { OriginalTariffSha256=new string('0',64) })
                .VerifySourceFingerprint());
    }
}
