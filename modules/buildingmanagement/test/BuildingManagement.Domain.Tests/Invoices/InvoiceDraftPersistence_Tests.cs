using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices.Tests;

/// <summary>
/// Tests the draft-only aggregate BEFORE any database migration or issue API.
/// </summary>
public sealed class InvoiceDraftPersistence_Tests
{
    private static InvoiceDraftSnapshot Snapshot(
        bool bothPriced = true,
        decimal? secondPrice = 15000m)
    {
        var electricity = Line(UtilityType.Electricity, 10m, 3500m);
        var water = bothPriced
            ? Line(UtilityType.Water, 5m, secondPrice)
            : Line(UtilityType.Water, 5m, null);
        return InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[] { electricity, water });
    }

    private static InvoiceDraftUtilityLineSnapshot Line(
        UtilityType type, decimal usage, decimal? price)
    {
        var source = new InvoiceDraftMeterSegmentSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), MeterUsageSourceType.MonthlyReading,
            new DateTime(2026, 10, 31), 100m, 100m + usage);
        return new InvoiceDraftUtilityLineSnapshot(
            type, price.HasValue ? Guid.NewGuid() : null,
            price.HasValue ? "SingleRateWholeMonth" : "NoRate",
            usage, price, price * usage,
            new[] { source }, Array.Empty<string>());
    }

    [Fact]
    public void Header_Is_Draft_Only_And_Has_Contract_Period_Key()
    {
        var source = Snapshot();
        var invoice = Invoice.CreateDraft(Guid.NewGuid(), source, Guid.NewGuid);

        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Equal(source.ContractId, invoice.ContractId);
        Assert.Equal(source.RoomId, invoice.RoomId);
        Assert.Equal(2026, invoice.BillingYear);
        Assert.Equal(10, invoice.BillingMonth);
        Assert.Equal(110000m, invoice.ProvisionalUtilityTotal);
        Assert.Equal(2, invoice.Items.Count);
    }

    [Fact]
    public void Each_Line_Preserves_Historical_Meter_Source_And_Price()
    {
        var invoice = Invoice.CreateDraft(
            Guid.NewGuid(), Snapshot(), Guid.NewGuid);

        var electricity = invoice.Items.Single(x =>
            x.UtilityType == UtilityType.Electricity);
        Assert.Equal(invoice.Id, electricity.InvoiceId);
        Assert.Equal(3500m, electricity.UnitPrice);
        Assert.Equal(10m, electricity.RecordedQuantity);
        Assert.Equal(35000m, electricity.ProvisionalAmount);
        var source = Assert.Single(electricity.MeterSources);
        Assert.Equal(electricity.Id, source.InvoiceItemId);
        Assert.Equal(MeterUsageSourceType.MonthlyReading, source.SourceType);
        Assert.Equal(10m, source.Quantity);
        Assert.NotEqual(Guid.Empty, source.SourceId);
        Assert.NotEqual(Guid.Empty, source.UtilityMeterId);
    }

    [Fact]
    public void Unpriced_Water_Keeps_Null_Not_Zero_Total()
    {
        var invoice = Invoice.CreateDraft(
            Guid.NewGuid(), Snapshot(bothPriced: false), Guid.NewGuid());

        Assert.Null(invoice.ProvisionalUtilityTotal);
        Assert.Null(invoice.Items.Single(x =>
            x.UtilityType == UtilityType.Water).ProvisionalAmount);
    }

    [Fact]
    public void Reject_Duplicate_Child_Ids_To_Avoid_Corrupt_Aggregate()
    {
        var sameId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            Invoice.CreateDraft(Guid.NewGuid(), Snapshot(), () => sameId));
    }

    [Fact]
    public void Reject_Empty_Invoice_Id()
    {
        Assert.Throws<ArgumentException>(() =>
            Invoice.CreateDraft(Guid.Empty, Snapshot(), Guid.NewGuid));
    }

    [Fact]
    public void Child_Collections_Cannot_Be_Modified_By_Caller()
    {
        var invoice = Invoice.CreateDraft(
            Guid.NewGuid(), Snapshot(), Guid.NewGuid);
        var items = Assert.IsAssignableFrom<ICollection<InvoiceItem>>(
            invoice.Items);
        Assert.Throws<NotSupportedException>(() => items.Clear());

        var one = invoice.Items.First();
        var sourceList = Assert.IsAssignableFrom<ICollection<InvoiceItemMeterSource>>(
            one.MeterSources);
        Assert.Throws<NotSupportedException>(() => sourceList.Clear());
        Assert.Equal(2, invoice.Items.Count);
    }

    [Fact]
    public void Aggregate_Has_No_Financial_Posting_Method()
    {
        var publicMethods = typeof(Invoice).GetMethods()
            .Select(x => x.Name).ToArray();

        Assert.DoesNotContain("Issue", publicMethods);
        Assert.DoesNotContain("Post", publicMethods);
        Assert.DoesNotContain("Pay", publicMethods);
    }
}
