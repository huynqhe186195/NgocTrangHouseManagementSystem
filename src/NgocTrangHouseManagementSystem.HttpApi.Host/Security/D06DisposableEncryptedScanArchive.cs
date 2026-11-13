using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Encrypted at-rest store for the D06 signed handover scan. This is the
/// same shape as the D04-B disposable vault but a SEPARATE key ring and
/// separate directory: a handover sheet is documentary evidence, not a
/// bank statement, and must not share a key with financial evidence.
///
/// The opaqueness of the returned handle is the only thing the database
/// stores; the bytes never appear in any table, log or response body beyond
/// the Owner's own authenticated download.
/// </summary>
public interface ID06SignedScanArchive
{
    Task<D06SignedScanReceipt> StoreAndVerifyAsync(
        Guid requestId, Guid uploadedByUserId, byte[] document,
        string mediaType, CancellationToken ct = default);

    Task<byte[]?> TryOpenAsync(
        Guid requestId, Guid uploadedByUserId, string reference,
        CancellationToken ct = default);
}

public sealed record D06SignedScanReceipt(
    string Reference, string DocumentSha256, int ByteLength);

public sealed class D06DisposableEncryptedScanArchive : ID06SignedScanArchive
{
    private readonly string _root;
    private readonly byte[] _key;

    public D06DisposableEncryptedScanArchive(string root, byte[] key)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("AES-256 key required.", nameof(key));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException(
                "Absolute archive directory required.", nameof(root));
        _root = Path.GetFullPath(root);
        _key = (byte[])key.Clone();
        Directory.CreateDirectory(_root);
    }

    public async Task<D06SignedScanReceipt> StoreAndVerifyAsync(
        Guid requestId, Guid uploadedByUserId, byte[] document,
        string mediaType, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || uploadedByUserId == Guid.Empty ||
            document is null || document.Length is < 256 or > 5_242_880)
            throw new InvalidOperationException("Invalid D06 signed scan.");
        RequireAllowedFileType(document, mediaType);
        var sha = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
        var reference = Guid.NewGuid().ToString("N");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[document.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(nonce, document, ciphertext, tag,
                Binding(requestId, uploadedByUserId));
        try
        {
            var path = Path.Combine(_root, reference + ".d06scan");
            await using var file = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            await file.WriteAsync(nonce, ct);
            await file.WriteAsync(tag, ct);
            await file.WriteAsync(ciphertext, ct);
            await file.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
        var readback = await TryOpenAsync(
            requestId, uploadedByUserId, reference, ct);
        if (readback is null ||
            Convert.ToHexString(SHA256.HashData(readback)).ToLowerInvariant() != sha)
            throw new CryptographicException("D06 scan archive readback failed.");
        return new D06SignedScanReceipt(reference, sha, document.Length);
    }

    public async Task<byte[]?> TryOpenAsync(
        Guid requestId, Guid uploadedByUserId, string reference,
        CancellationToken ct = default)
    {
        if (requestId == Guid.Empty || uploadedByUserId == Guid.Empty ||
            reference is null || reference.Length != 32 ||
            !Guid.TryParseExact(reference, "N", out _))
            return null;
        var path = Path.Combine(_root, reference + ".d06scan");
        try
        {
            await using var file = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (file.Length is < 284 or > 5_242_908) return null;
            var blob = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(blob, ct);
            var nonce = blob.AsSpan(0, 12);
            var tag = blob.AsSpan(12, 16);
            var ciphertext = blob.AsSpan(28);
            var plain = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(nonce, ciphertext, tag, plain,
                    Binding(requestId, uploadedByUserId));
                return plain;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plain);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Binding(Guid requestId, Guid uploadedByUserId)
        => Encoding.UTF8.GetBytes(
            "d06scan:" + requestId.ToString("D") + ":" +
            uploadedByUserId.ToString("D"));

    private static void RequireAllowedFileType(byte[] document, string mediaType)
    {
        var pdf = document.Length > 5 &&
            document[0] == 0x25 && document[1] == 0x50 &&
            document[2] == 0x44 && document[3] == 0x46;
        var png = document.Length > 8 &&
            document[0] == 0x89 && document[1] == 0x50 &&
            document[2] == 0x4E && document[3] == 0x47;
        var jpeg = document.Length > 3 &&
            document[0] == 0xFF && document[1] == 0xD8 && document[2] == 0xFF;
        var ok = mediaType switch
        {
            "application/pdf" => pdf,
            "image/png" => png,
            "image/jpeg" => jpeg,
            _ => false
        };
        if (!ok)
            throw new InvalidOperationException(
                "D06 scan must be a genuine PDF, PNG or JPEG document.");
    }
}
