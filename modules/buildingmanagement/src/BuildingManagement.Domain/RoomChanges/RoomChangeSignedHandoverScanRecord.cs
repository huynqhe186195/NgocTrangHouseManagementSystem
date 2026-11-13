using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// D06: the signed handwritten handover document scanned by the Owner and
/// archived against an already-recorded three-party signoff. The Owner's
/// 2026-11-04 decision accepts a scanned signed document as the evidence;
/// cryptographic e-signature is explicitly NOT required now.
///
/// This record carries the digest and media type ONLY. The raw scan never
/// enters this table or any log; it lives in the encrypted evidence vault
/// under a reference. A scanner upload is documentary attestation — it does
/// not prove signer identity, does not settle money and does not execute
/// the room change.
/// </summary>
public sealed class RoomChangeSignedHandoverScanRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid HandoverArchiveId { get; private set; }
    public string HandoverEvidenceSha256 { get; private set; } = string.Empty;
    public string HandoverDocumentSha256 { get; private set; } = string.Empty;
    public string MediaType { get; private set; } = string.Empty;
    public int ByteLength { get; private set; }
    public string VaultReference { get; private set; } = string.Empty;
    public Guid UploadedByUserId { get; private set; }
    public DateTime UploadedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeSignedHandoverScanRecord() { }
    private RoomChangeSignedHandoverScanRecord(Guid id) : base(id) { }

    public static RoomChangeSignedHandoverScanRecord Capture(
        Guid id, RoomChangeRequest request,
        RoomChangeEvidenceArchiveRecord handoverArchive,
        string documentSha256, string? mediaType, int byteLength,
        string? vaultReference, Guid uploadedByUserId, DateTime uploadedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handoverArchive);
        var media = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        var vault = vaultReference?.Trim() ?? string.Empty;
        if (id == Guid.Empty || request.Id == Guid.Empty ||
            request.SourceContractId == Guid.Empty ||
            request.BuildingId == Guid.Empty ||
            handoverArchive.Id == Guid.Empty ||
            handoverArchive.Kind != RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover ||
            handoverArchive.RequestId != request.Id ||
            handoverArchive.SourceContractId != request.SourceContractId ||
            handoverArchive.BuildingId != request.BuildingId ||
            !Sha(documentSha256) || !Sha(handoverArchive.RecordSha256) ||
            !IsAcceptedMediaType(media) ||
            byteLength is < 256 or > 5_242_880 ||
            !ValidVaultReference(vault) ||
            uploadedByUserId == Guid.Empty ||
            uploadedAtUtc.Kind != DateTimeKind.Utc ||
            uploadedAtUtc < handoverArchive.ArchivedAtUtc)
            throw new InvalidOperationException(
                "D06 scan must be a real PDF/PNG/JPEG document bound to the archived three-party handover.");

        var record = new RoomChangeSignedHandoverScanRecord(id)
        {
            RequestId = request.Id,
            SourceContractId = request.SourceContractId,
            BuildingId = request.BuildingId,
            HandoverArchiveId = handoverArchive.Id,
            HandoverEvidenceSha256 = handoverArchive.RecordSha256,
            HandoverDocumentSha256 = documentSha256.ToLowerInvariant(),
            MediaType = media,
            ByteLength = byteLength,
            VaultReference = vault,
            UploadedByUserId = uploadedByUserId,
            UploadedAtUtc = uploadedAtUtc.AddTicks(-(uploadedAtUtc.Ticks % 10L))
        };
        record.EvidenceSha256 = record.Hash();
        return record;
    }

    public void Verify(
        RoomChangeRequest request, RoomChangeEvidenceArchiveRecord handoverArchive)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handoverArchive);
        if (Id == Guid.Empty || EvidenceSha256 != Hash() ||
            RequestId != request.Id ||
            SourceContractId != request.SourceContractId ||
            BuildingId != request.BuildingId ||
            HandoverArchiveId != handoverArchive.Id ||
            HandoverEvidenceSha256 != handoverArchive.RecordSha256 ||
            handoverArchive.Kind != RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover ||
            !Sha(HandoverDocumentSha256) || !IsAcceptedMediaType(MediaType) ||
            ByteLength is < 256 or > 5_242_880 ||
            !ValidVaultReference(VaultReference) ||
            UploadedByUserId == Guid.Empty || UploadedAtUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("D06 signed scan has drifted from its handover archive.");
    }

    /// <summary>
    /// Only the three formats the local vault can prove by magic bytes are
    /// accepted. A scanned handwritten sheet is normally PDF or JPEG; PNG
    /// is allowed because phone photos of a signed page are common.
    /// </summary>
    public static bool IsAcceptedMediaType(string? mediaType) =>
        mediaType is "application/pdf" or "image/png" or "image/jpeg";

    /// <summary>
    /// The vault reference is an opaque 32-hex handle produced by the
    /// protected archive; it is not a path and never a URL.
    /// </summary>
    private static bool ValidVaultReference(string value)
        => value.Length == 32 &&
            Array.TrueForAll(value.ToCharArray(), c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Sha(string? value)
        => !string.IsNullOrEmpty(value) && value.Length == 64 &&
            Array.TrueForAll(value.ToCharArray(), c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), BuildingId.ToString("D"),
            HandoverArchiveId.ToString("D"), HandoverEvidenceSha256,
            HandoverDocumentSha256, MediaType,
            ByteLength.ToString(CultureInfo.InvariantCulture), VaultReference,
            UploadedByUserId.ToString("D"),
            UploadedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public bool ProvesSignerIdentity => false;
    public bool CanExecuteRoomChange => false;
}
