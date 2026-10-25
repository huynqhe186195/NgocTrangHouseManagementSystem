using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Offline-format archive package for a future append-only revision ledger.
/// SHA-256 detects accidental payload modifications if its stored digest is
/// protected separately; it is NOT a digital signature or authorization.
/// </summary>
public sealed record InvoiceDraftRevisionArchivePackage(
    string EvidenceJson, string EvidenceSha256);

/// <summary>
/// Versioned, canonical JSON encoding of an archived PREVIOUS draft, including
/// actor, reason, timestamp, tariff prices, all physical meter-source IDs and
/// exact counters. Does not query or write databases, rebuild or issue drafts.
/// </summary>
public static class InvoiceDraftRevisionArchiveCodec
{
    private const int FormatVersion = 1;

    public static InvoiceDraftRevisionArchivePackage Pack(
        InvoiceDraftRevisionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var old = evidence.PreviousDraft;
        var payload = new ArchivePayload(
            FormatVersion,
            evidence.InvoiceId,
            evidence.ArchivedRevision,
            evidence.ArchivedByUserId,
            evidence.ArchivedAtUtc,
            evidence.Reason,
            old.ContractId,
            old.RoomId,
            old.BillingYear,
            old.BillingMonth,
            old.ProvisionalTotal,
            old.UtilityLines.OrderBy(x => x.UtilityType)
                .Select(x => new ArchiveLine(
                    x.UtilityType, x.UtilityRateId, x.RateStatus,
                    x.Quantity, x.UnitPrice, x.ProvisionalAmount,
                    x.CoverageIssues.OrderBy(y => y, StringComparer.Ordinal).ToArray(),
                    x.MeterSegments
                        .OrderBy(y => y.SourceType)
                        .ThenBy(y => y.SourceId)
                        .Select(y => new ArchiveMeterSource(
                            y.SourceId, y.UtilityMeterId, y.SourceType,
                            y.RecordedDate, y.PreviousReading, y.CurrentReading))
                        .ToArray()))
                .ToArray());
        var json = JsonSerializer.Serialize(payload);
        return new InvoiceDraftRevisionArchivePackage(json, ComputeChecksum(json));
    }

    public static InvoiceDraftRevisionEvidence Unpack(
        InvoiceDraftRevisionArchivePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.EvidenceJson) ||
            package.EvidenceJson.Length > 1_000_000 ||
            package.EvidenceSha256 is null ||
            package.EvidenceSha256.Length != 64)
            throw new InvalidDataException("Draft revision archive is missing or exceeds limits.");

        byte[] expectedDigest;
        try
        {
            expectedDigest = Convert.FromHexString(package.EvidenceSha256);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid revision archive checksum.", error);
        }

        var actualDigest = SHA256.HashData(Encoding.UTF8.GetBytes(package.EvidenceJson));
        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            throw new InvalidDataException("Draft revision archive checksum mismatch.");

        ArchivePayload? stored;
        try
        {
            stored = JsonSerializer.Deserialize<ArchivePayload>(package.EvidenceJson);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Malformed revision archive JSON.", error);
        }
        if (stored is null || stored.FormatVersion != FormatVersion ||
            stored.UtilityLines is null ||
            stored.UtilityLines.Length != 2 ||
            stored.UtilityLines.Any(x => x is null ||
                                         x.CoverageIssues is null ||
                                         x.MeterSources is null ||
                                         x.MeterSources.Any(y => y is null)))
            throw new InvalidDataException("Unsupported or incomplete revision archive.");

        try
        {
            var oldLines = stored.UtilityLines.Select(x =>
                new InvoiceDraftUtilityLineSnapshot(
                    x.UtilityType,
                    x.UtilityRateId,
                    x.RateStatus,
                    x.Quantity,
                    x.UnitPrice,
                    x.ProvisionalAmount,
                    x.MeterSources.Select(y =>
                        new InvoiceDraftMeterSegmentSnapshot(
                            y.SourceId, y.UtilityMeterId, y.SourceType,
                            y.RecordedDate, y.PreviousReading, y.CurrentReading)),
                    x.CoverageIssues)).ToArray();
            var old = InvoiceDraftSnapshot.Capture(
                stored.ContractId, stored.RoomId,
                stored.BillingYear, stored.BillingMonth, oldLines);

            if (old.ProvisionalTotal != stored.ProvisionalTotal)
                throw new InvalidDataException("Revision archive monetary total mismatch.");

            var evidence = InvoiceDraftRevisionEvidence.Capture(
                stored.InvoiceId, stored.ArchivedRevision,
                stored.ArchivedByUserId, stored.ArchivedAtUtc,
                stored.Reason, old);
            // Accept only the exact canonical representation this codec writes.
            // Reject whitespace, property reordering, altered number rendering
            // or other unrecognized JSON forms even with a recalculated digest.
            if (Pack(evidence).EvidenceJson != package.EvidenceJson)
                throw new InvalidDataException("Revision archive is not canonical.");
            return evidence;
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid revision archive evidence.", error);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Revision archive amount overflow.", error);
        }
        catch (NullReferenceException error)
        {
            throw new InvalidDataException("Revision archive has missing fields.", error);
        }
    }

    /// <summary>
    /// Deterministic hexadecimal SHA-256 checksum of the exact UTF-8 bytes.
    /// This is a data-integrity checksum, not a security signature.
    /// </summary>
    public static string ComputeChecksum(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private sealed record ArchivePayload(
        int FormatVersion, Guid InvoiceId, int ArchivedRevision,
        Guid ArchivedByUserId, DateTime ArchivedAtUtc, string Reason,
        Guid ContractId, Guid RoomId, int BillingYear, int BillingMonth,
        decimal? ProvisionalTotal, ArchiveLine[] UtilityLines);

    private sealed record ArchiveLine(
        UtilityType UtilityType, Guid? UtilityRateId, string RateStatus,
        decimal Quantity, decimal? UnitPrice, decimal? ProvisionalAmount,
        string[] CoverageIssues, ArchiveMeterSource[] MeterSources);

    private sealed record ArchiveMeterSource(
        Guid SourceId, Guid UtilityMeterId, MeterUsageSourceType SourceType,
        DateTime RecordedDate, decimal PreviousReading, decimal CurrentReading);
}
