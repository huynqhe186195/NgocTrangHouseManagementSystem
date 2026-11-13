using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Owner contract document download trail. Every successful export writes
/// exactly one append-only row naming the actor, the authorized building,
/// the exact contract and a digest of the delivered bytes. It records a
/// download; it does not grant access, does not re-authorize a later read
/// and never contains the document itself.
/// </summary>
public sealed class RoomChangeContractDocumentAccessRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid AccessedByUserId { get; private set; }
    public DateTime AccessedAtUtc { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public string DeliveredDocumentSha256 { get; private set; } = string.Empty;
    public int DeliveredByteLength { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeContractDocumentAccessRecord() { }
    private RoomChangeContractDocumentAccessRecord(Guid id) : base(id) { }

    public static RoomChangeContractDocumentAccessRecord Record(
        Guid id, Guid contractId, Guid buildingId, Guid accessedByUserId,
        string? purpose, string deliveredDocumentSha256,
        int deliveredByteLength, DateTime accessedAtUtc)
    {
        var text = purpose?.Trim() ?? string.Empty;
        if (id == Guid.Empty || contractId == Guid.Empty ||
            buildingId == Guid.Empty || accessedByUserId == Guid.Empty ||
            text.Length is < 12 or > 300 ||
            !Sha(deliveredDocumentSha256) ||
            deliveredByteLength < 1024 ||
            accessedAtUtc.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                "Contract document access needs an authorized contract, a stated purpose and the delivered file digest.");

        var record = new RoomChangeContractDocumentAccessRecord(id)
        {
            ContractId = contractId,
            BuildingId = buildingId,
            AccessedByUserId = accessedByUserId,
            AccessedAtUtc = accessedAtUtc.AddTicks(-(accessedAtUtc.Ticks % 10L)),
            Purpose = text,
            DeliveredDocumentSha256 = deliveredDocumentSha256.ToLowerInvariant(),
            DeliveredByteLength = deliveredByteLength
        };
        record.EvidenceSha256 = record.Hash();
        return record;
    }

    public void Verify(Guid contractId, Guid buildingId)
    {
        if (Id == Guid.Empty || EvidenceSha256 != Hash() ||
            ContractId != contractId || BuildingId != buildingId ||
            AccessedByUserId == Guid.Empty ||
            AccessedAtUtc.Kind != DateTimeKind.Utc ||
            Purpose.Length is < 12 or > 300 ||
            !Sha(DeliveredDocumentSha256) || DeliveredByteLength < 1024)
            throw new InvalidDataException("Contract document access trail has drifted.");
    }

    private static bool Sha(string? value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 &&
            Array.TrueForAll(value.ToCharArray(), c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), ContractId.ToString("D"),
            BuildingId.ToString("D"), AccessedByUserId.ToString("D"),
            AccessedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Purpose, DeliveredDocumentSha256,
            DeliveredByteLength.ToString(CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public bool GrantsAccess => false;
}
