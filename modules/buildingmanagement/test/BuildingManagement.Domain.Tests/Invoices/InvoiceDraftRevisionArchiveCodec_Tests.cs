using System;
using System.IO;
using System.Linq;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices.Tests;

/// <summary>
/// Canonical versioned archive payload roundtrip tests: no DB I/O and
/// no mutation/issuance of any current invoice.
/// </summary>
public sealed class InvoiceDraftRevisionArchiveCodec_Tests
{
    private static readonly DateTime Timestamp =
        new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ARCH01_Roundtrip_Preserves_Actor_Metadata_And_Original_Source()
    {
        var source = OldSnapshot();
        var invoiceId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var evidence = InvoiceDraftRevisionEvidence.Capture(
            invoiceId, 2, operatorId, Timestamp,
            "Tariff was corrected after independent review", source);

        var packed = InvoiceDraftRevisionArchiveCodec.Pack(evidence);
        var restored = InvoiceDraftRevisionArchiveCodec.Unpack(packed);

        Assert.Equal(invoiceId, restored.InvoiceId);
        Assert.Equal(2, restored.ArchivedRevision);
        Assert.Equal(operatorId, restored.ArchivedByUserId);
        Assert.Equal(Timestamp, restored.ArchivedAtUtc);
        Assert.Equal(evidence.Reason, restored.Reason);
        Assert.Equal(source.ContractId, restored.PreviousDraft.ContractId);
        Assert.Equal(source.RoomId, restored.PreviousDraft.RoomId);
        Assert.Equal(2026, restored.PreviousDraft.BillingYear);
        Assert.Equal(10, restored.PreviousDraft.BillingMonth);
        Assert.Equal(110000m, restored.PreviousDraft.ProvisionalTotal);

        foreach (var line in source.UtilityLines)
        {
            var prior = restored.PreviousDraft.UtilityLines
                .Single(x => x.UtilityType == line.UtilityType);
            Assert.Equal(line.UtilityRateId, prior.UtilityRateId);
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
    public void ARCH02_Canonical_Payload_Is_Stable_Regardless_Of_Line_Order()
    {
        var prior = OldSnapshot();
        var swapped = InvoiceDraftSnapshot.Capture(
            prior.ContractId, prior.RoomId, 2026, 10,
            prior.UtilityLines.Reverse());
        var archiveId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        var a = InvoiceDraftRevisionArchiveCodec.Pack(
            InvoiceDraftRevisionEvidence.Capture(
                archiveId, 1, actor, Timestamp,
                "Verified changes in tariff configuration", prior));
        var b = InvoiceDraftRevisionArchiveCodec.Pack(
            InvoiceDraftRevisionEvidence.Capture(
                archiveId, 1, actor, Timestamp,
                "Verified changes in tariff configuration", swapped));
        Assert.Equal(a.EvidenceJson, b.EvidenceJson);
        Assert.Equal(a.EvidenceSha256, b.EvidenceSha256);
    }

    [Fact]
    public void ARCH03_Corrupted_Content_Is_Detected_By_Digest()
    {
        var packed = Pack();
        var modified = packed with
        {
            EvidenceJson = packed.EvidenceJson.Replace(
                "110000", "910000", StringComparison.Ordinal)
        };
        Assert.NotEqual(packed.EvidenceJson, modified.EvidenceJson);
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(modified));
    }

    [Fact]
    public void ARCH04_Invalid_Or_Empty_Checksum_Is_Rejected()
    {
        var a = Pack();
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(
                a with { EvidenceSha256 = "abc" }));
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(
                a with { EvidenceSha256 = new string('Q', 64) }));
    }

    [Fact]
    public void ARCH05_Related_Operator_Or_Reason_Change_Changes_Hash()
    {
        var id = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var snapshot = OldSnapshot();
        InvoiceDraftRevisionArchivePackage Archive(Guid user, string reason) =>
            InvoiceDraftRevisionArchiveCodec.Pack(
                InvoiceDraftRevisionEvidence.Capture(id, 1, user, Timestamp,
                    reason, snapshot));

        var original = Archive(actor, "Tariff corrected after full validation");
        Assert.NotEqual(original.EvidenceSha256,
            Archive(Guid.NewGuid(), "Tariff corrected after full validation")
                .EvidenceSha256);
        Assert.NotEqual(original.EvidenceSha256,
            Archive(actor, "Meter reading corrected after validation")
                .EvidenceSha256);
    }

    [Fact]
    public void ARCH06_Unknown_Schema_Version_Is_Rejected_Even_With_Valid_Checksum()
    {
        var package = Pack();
        var altered = package.EvidenceJson.Replace(
            "\"FormatVersion\":1", "\"FormatVersion\":9", StringComparison.Ordinal);
        Assert.NotEqual(package.EvidenceJson, altered);
        var withHash = InvoiceDraftRevisionArchiveCodec.ComputeChecksum(altered);
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(
                new InvoiceDraftRevisionArchivePackage(altered, withHash)));
    }

    [Fact]
    public void ARCH07_Invalid_JSON_Is_Rejected_Even_With_Matching_Hash()
    {
        const string malformedJson = "{this is not valid JSON}";
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(
                new InvoiceDraftRevisionArchivePackage(
                    malformedJson,
                    InvoiceDraftRevisionArchiveCodec.ComputeChecksum(
                        malformedJson))));
    }

    [Fact]
    public void ARCH08_Noncanonical_Payload_Is_Rejected_Even_If_Rehashed()
    {
        var a = Pack();
        var padded = a.EvidenceJson + " ";
        Assert.Throws<InvalidDataException>(() =>
            InvoiceDraftRevisionArchiveCodec.Unpack(
                new InvoiceDraftRevisionArchivePackage(
                    padded, InvoiceDraftRevisionArchiveCodec.ComputeChecksum(padded))));
    }

    [Fact]
    public void ARCH09_Pack_Has_No_Effect_On_Old_In_Memory_Evidence()
    {
        var source = OldSnapshot();
        var before = source.ProvisionalTotal;
        var archive = InvoiceDraftRevisionEvidence.Capture(
            Guid.NewGuid(), 1, Guid.NewGuid(), Timestamp,
            "Full audit reason after source correction", source);
        var pack = InvoiceDraftRevisionArchiveCodec.Pack(archive);
        Assert.Equal(before, source.ProvisionalTotal);
        Assert.Equal(2, source.UtilityLines.Count);
        Assert.Equal(archive.ArchivedRevision, 1);
        Assert.NotEmpty(pack.EvidenceJson);
        Assert.Equal(64, pack.EvidenceSha256.Length);
    }

    private static InvoiceDraftRevisionArchivePackage Pack() =>
        InvoiceDraftRevisionArchiveCodec.Pack(
            InvoiceDraftRevisionEvidence.Capture(
                Guid.NewGuid(), 1, Guid.NewGuid(), Timestamp,
                "Repaired source after a documented reading correction",
                OldSnapshot()));

    private static InvoiceDraftSnapshot OldSnapshot()
    {
        InvoiceDraftUtilityLineSnapshot Line(UtilityType type,
            decimal previous, decimal current, decimal unitPrice)
        {
            var segment = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, current);
            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                current - previous, unitPrice,
                (current - previous) * unitPrice,
                new[] { segment }, Array.Empty<string>());
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
