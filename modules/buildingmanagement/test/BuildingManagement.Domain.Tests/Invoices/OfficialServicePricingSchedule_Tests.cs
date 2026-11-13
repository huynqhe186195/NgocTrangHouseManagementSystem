using System;
using System.IO;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.RoomChanges;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

/// <summary>
/// P2.2 / D03 — service pricing as it appears on an ISSUED monthly invoice:
/// priced by RoomId + ServiceId + effective tariff version + billing unit,
/// with occupant and meter quantities coming from verified sources, and
/// frozen so a later room or pricing edit cannot rewrite the invoice.
/// </summary>
public sealed class OfficialServicePricingSchedule_Tests
{
    private static readonly DateTime PreparedAt =
        new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Scene(
        RoomScopedServiceTariffVersion Tariff,
        MonthlyInvoiceCandidateLedgerRecord Candidate,
        MonthlyInvoiceIndependentReviewRecord Review,
        MonthlyIssuedInvoiceRecord Issued,
        RoomScopedServiceInvoiceSourceBinding Binding,
        RoomChangeRosterMember[] Residency);

    private static Scene Build(
        RoomScopedServiceBillingUnit unit = RoomScopedServiceBillingUnit.PerPerson,
        RoomScopedServiceChargingRule rule =
            RoomScopedServiceChargingRule.CalendarDayProration,
        decimal price = 150_000m, decimal? meterQuantity = null,
        int occupants = 2, bool noFee = false)
    {
        var room = Guid.NewGuid();
        var service = Guid.NewGuid();
        var tariff = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), room, service, 1, price, unit, rule,
            new DateOnly(2026, 11, 1), null, Guid.NewGuid(),
            new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc),
            "OWNER-SIGNED-ROOM-SERVICE-RATE-NOVEMBER-2026");

        var quote = tariff.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            occupants, meterQuantity);

        var meterKind = Enum.GetValues<MeterUsageSourceType>().First();
        var power = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Electricity, Guid.NewGuid(), 10m, 3500m, 35_000m,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 11, 5), 100m, 110m) });
        var water = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Water, Guid.NewGuid(), 1m, 15000m, 15_000m,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), meterKind,
                new DateTime(2026, 11, 5), 55m, 56m) });

        var fee = new FinalInvoiceAuxiliaryChargeProposal(
            FinalInvoiceAuxiliaryFeeKind.Internet, "INTERNET",
            unit == RoomScopedServiceBillingUnit.MeteredUnit
                ? meterQuantity!.Value : occupants,
            unit == RoomScopedServiceBillingUnit.PerPerson
                ? tariff.UnitPriceVnd
                : quote.ProposedChargeVnd / (unit == RoomScopedServiceBillingUnit.MeteredUnit
                    ? meterQuantity!.Value : 1m),
            quote.ProposedChargeVnd, tariff.SignedPolicyReference,
            "Owner-approved internet service line for the billed month");

        var snapshot = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), room, Guid.NewGuid(), 2026, 11,
            Guid.NewGuid(), PreparedAt, new DateOnly(2026, 11, 20),
            "OWNER-APPROVED-DUE-DATE-POLICY-001",
            3_000_000m, 35_000m, 15_000m,
            noFee ? AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges
                  : AuxiliaryFeeAssessmentDecision.ItemizedCharges,
            "OWNER-APPROVED-PER-ROOM-SERVICE-POLICY-001",
            noFee ? Array.Empty<FinalInvoiceAuxiliaryChargeProposal>()
                  : new[] { fee },
            noFee ? 0m : fee.Amount,
            3_050_000m + (noFee ? 0m : fee.Amount),
            new[] { power, water });

        var candidate = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), snapshot);
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), candidate, Guid.NewGuid(),
            PreparedAt.AddHours(1), true, "Original rates and meter readings checked");
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202611-001", Guid.NewGuid(),
            PreparedAt.AddHours(2), candidate, review);

        var residency = new[]
        {
            new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.PrimaryTenant)
        }.Concat(Enumerable.Range(1, Math.Max(0, occupants - 1)).Select(_ =>
            new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.Member)))
         .ToArray();

        var binding = noFee ? null! : RoomScopedServiceInvoiceSourceBinding.Freeze(
            candidate, tariff, quote, "INTERNET",
            "VERIFIED-RESIDENCY-AND-SERVICE-USAGE-NOVEMBER-2026");

        return new Scene(tariff, candidate, review, issued, binding, residency);
    }

    // ---- happy path: the issued invoice really carries the service price ----

    [Fact]
    public void DOSVC01_Issued_Invoice_Carries_Per_Person_Pricing_From_Verified_Residency()
    {
        var scene = Build();
        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, new[] { scene.Binding },
            new[] { scene.Tariff }, scene.Residency);

        var line = Assert.Single(schedule.Lines);
        Assert.Equal(150_000m, line.UnitPriceVnd);
        Assert.Equal(RoomScopedServiceBillingUnit.PerPerson, line.BillingUnit);
        Assert.Equal(2, line.VerifiedOccupantCount);
        Assert.Equal(300_000m, line.AmountVnd);
        Assert.Equal(300_000m, schedule.TotalVnd);
        Assert.Equal(2, schedule.VerifiedResidencyCount);
        Assert.Equal(scene.Tariff.Id, line.TariffVersionId);
        Assert.Equal(scene.Tariff.EvidenceSha256, line.TariffSha256);
        schedule.VerifySchedule(scene.Candidate);
        Assert.False(schedule.IsOfficialReceivable);
        Assert.False(line.CanPostMoney);
    }

    [Fact]
    public void DOSVC02_Target_Room_Has_Its_Own_Rate_Not_The_Source_Room_Rate()
    {
        var scene = Build(price: 220_000m, occupants: 3);
        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, new[] { scene.Binding },
            new[] { scene.Tariff }, scene.Residency);

        Assert.Equal(220_000m, Assert.Single(schedule.Lines).UnitPriceVnd);
        Assert.Equal(660_000m, schedule.TotalVnd);
    }

    [Fact]
    public void DOSVC03_Metered_Service_Uses_Verified_Meter_Quantity()
    {
        var scene = Build(RoomScopedServiceBillingUnit.MeteredUnit,
            RoomScopedServiceChargingRule.VerifiedMeterUsage,
            price: 4_000m, meterQuantity: 12m, occupants: 2);
        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, new[] { scene.Binding },
            new[] { scene.Tariff }, scene.Residency);

        var line = Assert.Single(schedule.Lines);
        Assert.Equal(12m, line.VerifiedMeterQuantity);
        Assert.Equal(48_000m, line.AmountVnd);
        Assert.Equal(RoomScopedServiceBillingUnit.MeteredUnit, line.BillingUnit);
    }

    [Fact]
    public void DOSVC04_No_Fee_Invoice_Binds_An_Empty_Schedule()
    {
        var scene = Build(noFee: true);
        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, Array.Empty<RoomScopedServiceInvoiceSourceBinding>(),
            new[] { scene.Tariff }, scene.Residency);

        Assert.Empty(schedule.Lines);
        Assert.Equal(0m, schedule.TotalVnd);
        schedule.VerifySchedule(scene.Candidate);
    }

    // ---- immutability when the room or the pricing changes afterwards ----

    [Fact]
    public void DOSVC05_Appended_Future_Price_Cannot_Rewrite_An_Issued_Schedule()
    {
        var scene = Build();
        var later = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), scene.Tariff.RoomId, scene.Tariff.ServiceId, 2,
            900_000m, RoomScopedServiceBillingUnit.PerPerson,
            RoomScopedServiceChargingRule.CalendarDayProration,
            new DateOnly(2026, 12, 1), null, Guid.NewGuid(),
            new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc),
            "OWNER-SIGNED-ROOM-SERVICE-RATE-DECEMBER-2026");

        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, new[] { scene.Binding },
            new[] { scene.Tariff, later }, scene.Residency);

        var line = Assert.Single(schedule.Lines);
        Assert.Equal(scene.Tariff.Id, line.TariffVersionId);
        Assert.Equal(150_000m, line.UnitPriceVnd);
        Assert.Equal(300_000m, schedule.TotalVnd);
        schedule.VerifySchedule(scene.Candidate);
    }

    [Fact]
    public void DOSVC06_Price_Superseded_Before_Review_Is_Refused_Not_Billed()
    {
        var scene = Build();
        // A cheaper version the Owner approved BEFORE the candidate was
        // reviewed and that already covered the billed month: the binding
        // points at the stale rate, so issuance must refuse.
        var superseding = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), scene.Tariff.RoomId, scene.Tariff.ServiceId, 2,
            100_000m, RoomScopedServiceBillingUnit.PerPerson,
            RoomScopedServiceChargingRule.CalendarDayProration,
            new DateOnly(2026, 11, 1), null, Guid.NewGuid(),
            new DateTime(2026, 10, 25, 8, 0, 0, DateTimeKind.Utc),
            "OWNER-SIGNED-SUPERSEDING-NOVEMBER-RATE-AMENDMENT");

        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff, superseding }, scene.Residency));
    }

    [Fact]
    public void DOSVC07_Per_Person_Line_Must_Match_The_Verified_Residency_Count()
    {
        var scene = Build(occupants: 2);
        var understated = scene.Residency.Take(1).ToArray();
        var overstated = scene.Residency
            .Concat(new[] { new RoomChangeRosterMember(
                Guid.NewGuid(), ContractTenantRole.Member) }).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff }, understated));
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff }, overstated));
    }

    // ---- fail-closed boundaries ----

    [Fact]
    public void DOSVC08_Missing_Or_Foreign_Binding_Is_Refused()
    {
        var scene = Build();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate,
                Array.Empty<RoomScopedServiceInvoiceSourceBinding>(),
                new[] { scene.Tariff }, scene.Residency));

        var other = Build();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { other.Binding },
                new[] { scene.Tariff }, scene.Residency));
    }

    [Fact]
    public void DOSVC09_Issued_Invoice_Of_A_Different_Candidate_Is_Refused()
    {
        var scene = Build();
        var other = Build();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                other.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff }, scene.Residency));
    }

    [Fact]
    public void DOSVC10_Empty_Or_Malformed_Residency_Is_Refused()
    {
        var scene = Build();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff },
                Array.Empty<RoomChangeRosterMember>()));

        var twoPrimaries = scene.Residency.Select(x =>
            new RoomChangeRosterMember(x.TenantId, ContractTenantRole.PrimaryTenant))
            .ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialServicePricingSchedule.Bind(
                scene.Issued, scene.Candidate, new[] { scene.Binding },
                new[] { scene.Tariff }, twoPrimaries));
    }

    [Fact]
    public void DOSVC11_Schedule_Is_Refused_For_A_Candidate_It_Was_Not_Bound_To()
    {
        var scene = Build();
        var schedule = OfficialServicePricingSchedule.Bind(
            scene.Issued, scene.Candidate, new[] { scene.Binding },
            new[] { scene.Tariff }, scene.Residency);
        schedule.VerifySchedule(scene.Candidate);

        // A different candidate cannot claim another invoice's frozen schedule.
        var other = Build();
        Assert.Throws<InvalidDataException>(() =>
            schedule.VerifySchedule(other.Candidate));
    }
}
