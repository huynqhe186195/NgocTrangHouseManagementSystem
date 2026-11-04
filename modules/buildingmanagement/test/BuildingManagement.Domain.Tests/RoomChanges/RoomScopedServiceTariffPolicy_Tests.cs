using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomScopedServiceTariffPolicy_Tests
{
    private static RoomScopedServiceTariffVersion Tariff(
        Guid? room = null, Guid? service = null, int version = 1,
        decimal price = 150_000m,
        RoomScopedServiceBillingUnit unit =
            RoomScopedServiceBillingUnit.PerPerson,
        RoomScopedServiceChargingRule charge =
            RoomScopedServiceChargingRule.CalendarDayProration,
        DateOnly? from = null, DateOnly? through = null)
        => RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), room ?? Guid.NewGuid(),
            service ?? Guid.NewGuid(), version, price, unit, charge,
            from ?? new DateOnly(2026, 1, 1), through,
            Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "SIGNED-ROOM-SERVICE-TARIFF-VERSION-001");

    [Fact]
    public void TRF_SVC01_Room301_150k_PerPerson_Two_Residents()
    {
        var v = Tariff(charge: RoomScopedServiceChargingRule.WholeMonth);
        var p = v.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), 2);
        Assert.Equal(300_000m, p.ProposedChargeVnd);
        Assert.Equal(v.RoomId, p.RoomId);
        Assert.Equal(v.Id, p.TariffVersionId);
        Assert.False(p.IsIssuedInvoice);
        Assert.False(p.CanPost);
    }

    [Fact]
    public void TRF_SVC02_Another_Room_Can_Charge_Different_Rate()
    {
        var service = Guid.NewGuid();
        var room301 = Tariff(service: service, price: 150_000m,
            charge: RoomScopedServiceChargingRule.WholeMonth);
        var room302 = Tariff(service: service, price: 200_000m,
            charge: RoomScopedServiceChargingRule.WholeMonth);
        RoomScopedServiceTariffVersion.RequireNonOverlapping(room301, room302);
        var a = room301.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), 2);
        var b = room302.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), 2);
        Assert.Equal(300_000m, a.ProposedChargeVnd);
        Assert.Equal(400_000m, b.ProposedChargeVnd);
    }

    [Fact]
    public void TRF_SVC03_Transfer_Source14_Target16_Have_Different_Rates()
    {
        var source = Tariff(price: 150_000m);
        var target = Tariff(price: 200_000m);
        var a = source.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 14), 2);
        var b = target.PreviewCharge(
            new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 30), 2);
        Assert.Equal(140_000m, a.ProposedChargeVnd);
        Assert.Equal(213_333m, b.ProposedChargeVnd);
    }

    [Fact]
    public void TRF_SVC04_ThirtyOne_Day_Fee_Rounds_To_Whole_Vnd()
    {
        var v = Tariff(price: 150_000m);
        var p = v.PreviewCharge(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1);
        Assert.Equal(4_839m, p.ProposedChargeVnd);
    }

    [Fact]
    public void TRF_SVC05_Overlapping_Active_Room_Versions_Are_Denied()
    {
        var room = Guid.NewGuid();
        var service = Guid.NewGuid();
        var first = Tariff(room, service, version: 1,
            through: new DateOnly(2026, 11, 30));
        var overlapping = Tariff(room, service, version: 2,
            from: new DateOnly(2026, 11, 15));
        Assert.Throws<InvalidOperationException>(() =>
            RoomScopedServiceTariffVersion.RequireNonOverlapping(
                first, overlapping));
    }

    [Fact]
    public void TRF_SVC06_Prospective_Edit_Creates_Separate_History_Version()
    {
        var room = Guid.NewGuid();
        var service = Guid.NewGuid();
        var old = Tariff(room, service, price: 150_000m,
            through: new DateOnly(2026, 10, 31));
        var newer = Tariff(room, service, version: 2, price: 200_000m,
            from: new DateOnly(2026, 11, 1));
        RoomScopedServiceTariffVersion.RequireNonOverlapping(old, newer);
        var oldHash = old.EvidenceSha256;
        Assert.NotEqual(oldHash, newer.EvidenceSha256);
        old.VerifyFingerprint();
        newer.VerifyFingerprint();
        Assert.Equal(150_000m, old.UnitPriceVnd);
        Assert.Equal(200_000m, newer.UnitPriceVnd);
    }

    [Fact]
    public void TRF_SVC07_Modified_Archived_Rate_Detected()
    {
        var v = Tariff();
        Assert.Throws<InvalidOperationException>(() =>
            (v with { UnitPriceVnd = 1m }).VerifyFingerprint());
        Assert.Throws<InvalidOperationException>(() =>
            (v with { RoomId = Guid.NewGuid() }).VerifyFingerprint());
    }

    [Fact]
    public void TRF_SVC08_Metered_Service_Requires_Verified_Quantity()
    {
        var v = Tariff(price: 5_000m,
            unit: RoomScopedServiceBillingUnit.MeteredUnit,
            charge: RoomScopedServiceChargingRule.VerifiedMeterUsage);
        var a = v.PreviewCharge(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            2, 12m);
        Assert.Equal(60_000m, a.ProposedChargeVnd);
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 30), 2));
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 30), 2, -1m));
    }

    [Fact]
    public void TRF_SVC09_Headcount_Or_Unexpected_Meter_Fails()
    {
        var v = Tariff();
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 30), 0));
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 30), 2, 9m));
    }

    [Fact]
    public void TRF_SVC10_CrossMonth_Or_Expired_Version_Denied()
    {
        var v = Tariff(through: new DateOnly(2026, 11, 15));
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 12, 1), 2));
        Assert.Throws<InvalidOperationException>(() =>
            v.PreviewCharge(new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 16), 2));
    }
    [Fact]
    public void TRF_SVC11_Prospective_Append_Supersedes_OpenEnded_Old_Rate()
    {
        var room = Guid.NewGuid();
        var service = Guid.NewGuid();
        var original = Tariff(room, service, price: 150_000m,
            from: new DateOnly(2026, 1, 1));
        var edited = Tariff(room, service, version: 2, price: 200_000m,
            from: new DateOnly(2026, 12, 1));

        var november = RoomScopedServiceTariffVersion.SelectEffectiveForDate(
            new[] { original, edited }, room, service, new DateOnly(2026, 11, 15));
        var december = RoomScopedServiceTariffVersion.SelectEffectiveForDate(
            new[] { original, edited }, room, service, new DateOnly(2026, 12, 15));

        Assert.Equal(original.Id, november.Id);
        Assert.Equal(edited.Id, december.Id);
        Assert.Equal(150_000m, original.UnitPriceVnd);
        Assert.Equal(200_000m, edited.UnitPriceVnd);
        original.VerifyFingerprint();
    }

    [Fact]
    public void TRF_SVC12_EffectiveRate_Does_Not_Cross_Room_Boundaries()
    {
        var room301 = Guid.NewGuid();
        var room302 = Guid.NewGuid();
        var service = Guid.NewGuid();
        var a = Tariff(room301, service, price: 150_000m);
        var b = Tariff(room302, service, price: 200_000m);
        var selected = RoomScopedServiceTariffVersion.SelectEffectiveForDate(
            new[] { a, b }, room301, service, new DateOnly(2026, 11, 1));
        Assert.Equal(a.Id, selected.Id);
        Assert.Throws<InvalidOperationException>(() =>
            RoomScopedServiceTariffVersion.SelectEffectiveForDate(
                new[] { a }, room302, service, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void TRF_SVC13_Missing_Or_Tampered_Tariff_Is_Fail_Closed()
    {
        var a = Tariff();
        Assert.Throws<InvalidOperationException>(() =>
            RoomScopedServiceTariffVersion.SelectEffectiveForDate(
                new[] { a with { UnitPriceVnd = 999m } },
                a.RoomId, a.ServiceId, new DateOnly(2026, 11, 1)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomScopedServiceTariffVersion.SelectEffectiveForDate(
                new[] { a }, a.RoomId, a.ServiceId,
                new DateOnly(2025, 12, 31)));
    }

    [Fact]
    public void TRF_SVC15_Append_Newer_Price_Supersedes_Original_Open_Ended_History()
    {
        var room = Guid.NewGuid();
        var service = Guid.NewGuid();
        var original = Tariff(room, service, from: new DateOnly(2026, 1, 1));
        var successor = Tariff(room, service, version: 2, price: 200_000m,
            from: new DateOnly(2026, 12, 1));
        RoomScopedServiceTariffVersion.RequireNonOverlapping(original, successor);
        var selected = RoomScopedServiceTariffVersion.SelectEffectiveForDate(
            new[] { original, successor }, room, service, new DateOnly(2027, 1, 1));
        Assert.Equal(successor.Id, selected.Id);
        Assert.Equal(150_000m, original.UnitPriceVnd);
        original.VerifyFingerprint();
    }

    [Fact]
    public void TRF_SVC14_PostgreSql_Microsecond_Roundtrip_Preserves_SHA()
    {
        var at = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc)
            .AddTicks(1234567);
        var row = RoomScopedServiceTariffVersion.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            150_000m, RoomScopedServiceBillingUnit.PerPerson,
            RoomScopedServiceChargingRule.CalendarDayProration,
            new DateOnly(2026, 11, 5), null,
            Guid.NewGuid(), at, "SIGNED-ROOM-PRICE-MICROSECOND-REVIEW-2026");
        Assert.Equal(0L, row.ApprovedAtUtc.Ticks % 10L);
        row.VerifyFingerprint();
    }

}
