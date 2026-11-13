using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NgocTrangHouseManagementSystem.Security;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// D06 scanned signed handover sheet. Upload writes only the digest and an
/// opaque vault handle; the bytes live in a SEPARATE encrypted store from
/// bank evidence. The Owner's 2026-11-04 decision accepts a scanned signed
/// document as D06 evidence and does not require cryptographic e-signing.
///
/// Nothing here proves signer identity, executes the transfer, or touches
/// money. The three-party signoff archive must already exist, and the
/// uploader must be a currently scoped contract manager.
/// </summary>
public sealed partial class SliceBStagingRoomChangeRequests
{
    public Task<SliceBD06SignedScanRead> UploadD06SignedHandoverScanAsync(
        Guid contractId, Guid requestId, SliceBD06ScanUpload input,
        ID06SignedScanArchive archive, CancellationToken ct = default)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (input is null || archive is null ||
                input.Document is null || input.Document.Length is < 256 or > 5_242_880 ||
                !RoomChangeSignedHandoverScanRecord.IsAcceptedMediaType(input.MediaType) ||
                db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel !=
                    IsolationLevel.Serializable)
                throw Denied();

            var request = await LockRequestAsync(
                db, requestId, contractId, sourceRoom, building);
            var handover = await db.RoomChangeEvidenceArchives.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.Kind == RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover)
                ?? throw Denied();
            if (await db.RoomChangeSignedHandoverScans.AsNoTracking()
                    .AnyAsync(x => x.RequestId == requestId))
                throw Denied();

            // Store first so the row never names a reference that does not
            // exist; the vault readback inside StoreAndVerifyAsync is the
            // proof the bytes are durably retrievable before we expose them.
            var stored = await archive.StoreAndVerifyAsync(
                requestId, actor, input.Document!, input.MediaType!, ct);

            var record = RoomChangeSignedHandoverScanRecord.Capture(
                Guid.NewGuid(), request, handover,
                stored.DocumentSha256, input.MediaType,
                stored.ByteLength, stored.Reference,
                actor, _clock.GetUtcNow().UtcDateTime);
            record.Verify(request, handover);
            db.RoomChangeSignedHandoverScans.Add(record);
            await db.SaveChangesAsync(ct);
            return new SliceBD06SignedScanRead(
                record.Id, record.RequestId, record.HandoverArchiveId,
                record.HandoverDocumentSha256, record.MediaType,
                record.ByteLength, record.UploadedAtUtc,
                ProvesSignerIdentity: false, CanExecuteRoomChange: false,
                CanDownload: true);
        });
    public Task<SliceBD06SignedScanDownload> DownloadD06SignedHandoverScanAsync(
        Guid contractId, Guid requestId, ID06SignedScanArchive archive,
        CancellationToken ct = default)
        => ExecuteScopedAsync(contractId, async (db, actor, building, sourceRoom) =>
        {
            if (archive is null) throw Denied();
            await _identity.AcquireBootstrapAsync(actor);
            await LockRequestAsync(db, requestId, contractId, sourceRoom, building);
            var scan = await db.RoomChangeSignedHandoverScans.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == requestId &&
                    x.SourceContractId == contractId && x.BuildingId == building)
                ?? throw Denied();
            // The vault handle is bound to the uploader; the downloader is
            // the scoped Owner reading their own building's evidence, so the
            // vault is opened with the ORIGINAL uploader identity, which is
            // what the AES-GCM associated data requires.
            var bytes = await archive.TryOpenAsync(
                scan.RequestId, scan.UploadedByUserId, scan.VaultReference, ct)
                ?? throw Denied();
            return new SliceBD06SignedScanDownload(
                bytes, scan.MediaType,
                $"bien-ban-ban-giao-{requestId:N}.{Extension(scan.MediaType)}",
                scan.HandoverDocumentSha256);
        });

    private static string Extension(string mediaType) => mediaType switch
    {
        "application/pdf" => "pdf",
        "image/png" => "png",
        _ => "jpg"
    };
}

public sealed record SliceBD06ScanUpload(
    byte[]? Document, string? MediaType);

public sealed class SliceBD06ScanForm
{
    public Microsoft.AspNetCore.Http.IFormFile? Document { get; set; }
}

public sealed record SliceBD06SignedScanRead(
    Guid ScanId, Guid RequestId, Guid HandoverArchiveId,
    string DocumentSha256, string MediaType, int ByteLength,
    DateTime UploadedAtUtc, bool ProvesSignerIdentity,
    bool CanExecuteRoomChange, bool CanDownload);

public sealed record SliceBD06SignedScanDownload(
    byte[] Content, string MediaType, string FileName, string DocumentSha256);
