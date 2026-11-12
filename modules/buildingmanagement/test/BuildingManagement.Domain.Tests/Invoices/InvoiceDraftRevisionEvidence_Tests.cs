using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices.Tests;

/// <summary>
/// NON-DB audit evidence prototype. No stored invoice is edited, and
/// these tests do not approve a versioned rebuild or financial issuance.
/// </summary>
public sealed class InvoiceDraftRevisionEvidence_Tests
{
    private static readonly DateTime ArchiveTime =
        new(2026, 10, 25, 12, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void REV01_Archives_Original_Billing_Values_With_Operator_And_Reason()
    {
        var original = Snapshot();
        var invoiceId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();

        var evidence = InvoiceDraftRevisionEvidence.Capture(
            invoiceId, 1, operatorId, ArchiveTime,
            "  October tariff corrected after review  ", original);

        Assert.Equal(invoiceId, evidence.InvoiceId);
        Assert.Equal(1, evidence.ArchivedRevision);
        Assert.Equal(operatorId, evidence.ArchivedByUserId);
        Assert.Equal(ArchiveTime, evidence.ArchivedAtUtc);
        Assert.Equal("October tariff corrected after review", evidence.Reason);
        Assert.Equal(original.ContractId, evidence.PreviousDraft.ContractId);
        Assert.Equal(original.RoomId, evidence.PreviousDraft.RoomId);
        Assert.Equal(2026, evidence.PreviousDraft.BillingYear);
        Assert.Equal(10, evidence.PreviousDraft.BillingMonth);
        Assert.Equal(110000m, evidence.PreviousDraft.ProvisionalTotal);
    }

    [Fact]
    public void REV02_Copies_Old_Line_And_Physical_Meter_Evidence_By_Value()
    {
        var original = Snapshot();
        var archive = Archive(original);
        Assert.NotSame(original, archive.PreviousDraft);
        Assert.NotSame(original.UtilityLines, archive.PreviousDraft.UtilityLines);

        foreach (var line in original.UtilityLines)
        {
            var prior = archive.PreviousDraft.UtilityLines.Single(
                x => x.UtilityType == line.UtilityType);
            Assert.NotSame(line, prior);
            Assert.NotSame(line.MeterSegments, prior.MeterSegments);
            Assert.Equal(line.UnitPrice, prior.UnitPrice);
            Assert.Equal(line.Quantity, prior.Quantity);
            Assert.Equal(line.ProvisionalAmount, prior.ProvisionalAmount);

            var before = Assert.Single(line.MeterSegments);
            var after = Assert.Single(prior.MeterSegments);
            Assert.Equal(before.SourceId, after.SourceId);
            Assert.Equal(before.UtilityMeterId, after.UtilityMeterId);
            Assert.Equal(before.SourceType, after.SourceType);
            Assert.Equal(before.RecordedDate, after.RecordedDate);
            Assert.Equal(before.PreviousReading, after.PreviousReading);
            Assert.Equal(before.CurrentReading, after.CurrentReading);
        }
    }

    [Fact]
    public void REV03_Archived_Collections_Are_Not_Caller_Writable()
    {
        var archive = Archive(Snapshot());
        var lines = Assert.IsAssignableFrom<ICollection<InvoiceDraftUtilityLineSnapshot>>(
            archive.PreviousDraft.UtilityLines);
        Assert.Throws<NotSupportedException>(() => lines.Clear());

        var sources = Assert.IsAssignableFrom<ICollection<InvoiceDraftMeterSegmentSnapshot>>(
            archive.PreviousDraft.UtilityLines[0].MeterSegments);
        Assert.Throws<NotSupportedException>(() => sources.Clear());
        Assert.Equal(2, archive.PreviousDraft.UtilityLines.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void REV04_Rejects_Empty_Or_Uninformative_Correction_Reason(string reason)
    {
        Assert.Throws<ArgumentException>(() => InvoiceDraftRevisionEvidence.Capture(
            Guid.NewGuid(), 1, Guid.NewGuid(), ArchiveTime,
            reason, Snapshot()));
    }

    [Fact]
    public void REV05_Rejects_Overlong_Reason()
    {
        Assert.Throws<ArgumentException>(() => InvoiceDraftRevisionEvidence.Capture(
            Guid.NewGuid(), 1, Guid.NewGuid(), ArchiveTime,
            new string('r', 501), Snapshot()));
    }

    [Fact]
    public void REV06_Rejects_Missing_Invoice_Or_Actor_Identity()
    {
        var draft = Snapshot();
        Assert.Throws<ArgumentException>(() => InvoiceDraftRevisionEvidence.Capture(
            Guid.Empty, 1, Guid.NewGuid(), ArchiveTime,
            "Valid correction reason", draft));
        Assert.Throws<ArgumentException>(() => InvoiceDraftRevisionEvidence.Capture(
            Guid.NewGuid(), 1, Guid.Empty, ArchiveTime,
            "Valid correction reason", draft));
    }

    [Fact]
    public void REV07_Rejects_NonPositive_Revision_Number_And_NonUtc_Time()
    {
        var draft = Snapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InvoiceDraftRevisionEvidence.Capture(
                Guid.NewGuid(), 0, Guid.NewGuid(), ArchiveTime,
                "Valid correction reason", draft));
        Assert.Throws<ArgumentException>(() =>
            InvoiceDraftRevisionEvidence.Capture(
                Guid.NewGuid(), 1, Guid.NewGuid(),
                new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Local),
                "Valid correction reason", draft));
    }

    [Fact]
    public void REV08_Refuses_Unpriced_Prior_Utility_Draft()
    {
        var ready = Snapshot();
        var unpriced = new InvoiceDraftUtilityLineSnapshot(
            UtilityType.Water, null, "NoRate", 5m,
            null, null, new[]
            {
                new InvoiceDraftMeterSegmentSnapshot(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 10, 31),
                    100m, 105m)
            }, Array.Empty<string>());

        var invalid = InvoiceDraftSnapshot.Capture(
            ready.ContractId, ready.RoomId, 2026, 10,
            ready.UtilityLines.Where(x => x.UtilityType == UtilityType.Electricity)
                .Concat(new[] { unpriced }));

        Assert.Throws<ArgumentException>(() => Archive(invalid));
    }

    private static InvoiceDraftRevisionEvidence Archive(InvoiceDraftSnapshot snapshot) =>
        InvoiceDraftRevisionEvidence.Capture(
            Guid.NewGuid(), 1, Guid.NewGuid(), ArchiveTime,
            "Verified operator correction reason", snapshot);

    private static InvoiceDraftSnapshot Snapshot()
    {
        InvoiceDraftUtilityLineSnapshot Line(
            UtilityType type, decimal previous, decimal current, decimal price)
        {
            var source = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, current);

            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                current - previous, price,
                (current - previous) * price,
                new[] { source }, Array.Empty<string>());
        }

        return InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[]
            {
                Line(UtilityType.Electricity, 10m, 20m, 3500m),
                Line(UtilityType.Water, 100m, 105m, 15000m)
            });
    }
}
