using System;
using System.Collections.Generic;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices.Tests;

public sealed class InvoiceDraftSnapshot_Tests
{
    private static InvoiceDraftUtilityLineSnapshot Line(
        UtilityType utilityType = UtilityType.Electricity,
        decimal quantity = 5m,
        decimal? unitPrice = 3500m,
        decimal? amount = 17500m,
        string status = "SingleRateWholeMonth",
        IEnumerable<string>? issues = null)
    {
        var segment = new InvoiceDraftMeterSegmentSnapshot(
            Guid.NewGuid(), Guid.NewGuid(),
            MeterUsageSourceType.MonthlyReading,
            new DateTime(2026, 10, 31), 10m, 10m + quantity);

        return new InvoiceDraftUtilityLineSnapshot(
            utilityType, amount.HasValue ? Guid.NewGuid() : null,
            status, quantity, unitPrice, amount,
            new[] { segment }, issues ?? Array.Empty<string>());
    }

    [Fact]
    public void Complete_Contract_Bound_Snapshot_Copies_Price_And_Physical_Source()
    {
        var contract = Guid.NewGuid();
        var room = Guid.NewGuid();
        var line = Line();
        var snapshot = InvoiceDraftSnapshot.Capture(
            contract, room, 2026, 10, new[] { line });

        Assert.Equal(contract, snapshot.ContractId);
        Assert.Equal(room, snapshot.RoomId);
        Assert.Equal(17500m, snapshot.ProvisionalTotal);
        Assert.Equal(5m, snapshot.UtilityLines[0].Quantity);
        Assert.Equal(3500m, snapshot.UtilityLines[0].UnitPrice);
        Assert.Single(snapshot.UtilityLines[0].MeterSegments);
        Assert.NotEqual(Guid.Empty, snapshot.UtilityLines[0].MeterSegments[0].SourceId);
        Assert.False(snapshot.CanIssue);
        Assert.False(snapshot.IsPersisted);
    }

    [Fact]
    public void Missing_Rate_Does_Not_Become_Free_Electricity()
    {
        var line = Line(unitPrice: null, amount: null, status: "NoRate");
        var snapshot = InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[] { line });

        Assert.Null(snapshot.ProvisionalTotal);
        Assert.Null(snapshot.UtilityLines[0].ProvisionalAmount);
    }

    [Fact]
    public void Refuses_Amount_That_Does_Not_Match_Snapshotted_Quantity_And_Rate()
    {
        Assert.Throws<ArgumentException>(() =>
            Line(unitPrice: 3500m, amount: 3500m));
    }

    [Fact]
    public void Refuses_Pricing_When_Coverage_Has_Unresolved_Gaps()
    {
        Assert.Throws<ArgumentException>(() =>
            Line(issues: new[] { "MissingPreviousMonthReading" }));
    }

    [Fact]
    public void Refuses_Duplicated_Utility_Line()
    {
        var first = Line();
        var second = Line();
        Assert.Throws<ArgumentException>(() =>
            InvoiceDraftSnapshot.Capture(
                Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
                new[] { first, second }));
    }

    [Fact]
    public void Captured_Snapshot_Cannot_Change_When_Source_Lists_Are_Mutated()
    {
        var source = new List<InvoiceDraftMeterSegmentSnapshot>
        {
            new(Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), 10m, 15m)
        };
        var issueList = new List<string>();
        var line = new InvoiceDraftUtilityLineSnapshot(
            UtilityType.Water, Guid.NewGuid(), "SingleRateWholeMonth",
            5m, 1000m, 5000m, source, issueList);
        var snapshot = InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[] { line });

        source.Clear();
        issueList.Add("Mutated externally");

        Assert.Single(snapshot.UtilityLines[0].MeterSegments);
        Assert.Empty(snapshot.UtilityLines[0].CoverageIssues);
        Assert.Equal(5000m, snapshot.ProvisionalTotal);
    }

    [Fact]
    public void One_Unpriced_Line_Makes_Snapshot_Total_Unavailable()
    {
        var priced = Line();
        var pending = Line(
            utilityType: UtilityType.Water,
            unitPrice: null, amount: null, status: "MultipleRatesInMonth");
        var snapshot = InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[] { priced, pending });

        Assert.Null(snapshot.ProvisionalTotal);
        Assert.False(snapshot.CanIssue);
    }
}
