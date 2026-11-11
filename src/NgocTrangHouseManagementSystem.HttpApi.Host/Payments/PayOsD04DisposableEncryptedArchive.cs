using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Disposable localhost UAT ONLY. Encrypts independent bank evidence at rest,
/// creates files without overwrite, and authenticates bytes and actor binding
/// on every read. Never use for real bank documents or production.
///
/// P1.3: supports a two-key rotation ring — new files encrypt under the
/// CURRENT key while files written under the PREVIOUS key still verify
/// until re-archival. Also validates the document's file-type signature
/// (PNG/JPEG/PDF magic bytes) before encryption: only those three evidence
/// formats are accepted in this disposable lane.
/// </summary>
public sealed class PayOsD04DisposableEncryptedArchive : IPayOsD04ProtectedBankEvidenceArchive
{
    private readonly string _root;
    private readonly byte[] _key;
    private readonly byte[]? _previousKey;

    public PayOsD04DisposableEncryptedArchive(string root, byte[] key)
        : this(root, key, null)
    {
    }

    public PayOsD04DisposableEncryptedArchive(
        string root, byte[] key, byte[]? previousKey)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("AES-256 key required.", nameof(key));
        if (previousKey is not null && previousKey.Length != 32)
            throw new ArgumentException("AES-256 previous key required.", nameof(previousKey));
        if (previousKey is not null &&
            CryptographicOperations.FixedTimeEquals(previousKey, key))
            throw new ArgumentException(
                "Previous key must differ from the current key.", nameof(previousKey));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Absolute archive directory required.", nameof(root));
        _root = Path.GetFullPath(root);
        _key = (byte[])key.Clone();
        _previousKey = previousKey is null ? null : (byte[])previousKey.Clone();
        Directory.CreateDirectory(_root);
    }

    public async Task<PayOsD04ProtectedBankEvidenceReceipt> StoreAndVerifyAsync(
        Guid intentId, Guid reviewerId, byte[] document, CancellationToken ct = default)
    {
        if (intentId == Guid.Empty || reviewerId == Guid.Empty ||
            document is null || document.Length is < 256 or > 5_242_880)
            throw new InvalidOperationException("Invalid D04-B synthetic evidence.");
        RequireAllowedFileType(document);
        var sha = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
        var reference = Guid.NewGuid().ToString("N");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[document.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(nonce, document, ciphertext, tag, Binding(intentId, reviewerId));
        var path = Path.Combine(_root, reference + ".d04vault");
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough | FileOptions.Asynchronous);
            await file.WriteAsync(nonce, ct);
            await file.WriteAsync(tag, ct);
            await file.WriteAsync(ciphertext, ct);
            await file.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
        if (!await VerifyAsync(intentId, reviewerId, reference, sha, ct))
            throw new CryptographicException("D04-B archive readback failed.");
        return new PayOsD04ProtectedBankEvidenceReceipt(reference, sha);
    }

    public async Task<bool> VerifyAsync(Guid intentId, Guid reviewerId,
        string reference, string documentSha256, CancellationToken ct = default)
    {
        if (intentId == Guid.Empty || reviewerId == Guid.Empty ||
            reference is null || reference.Length != 32 ||
            !Guid.TryParseExact(reference, "N", out _) ||
            documentSha256 is null || documentSha256.Length != 64)
            return false;
        var path = Path.Combine(_root, reference + ".d04vault");
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous);
            if (file.Length is < 284 or > 5_242_908) return false;
            var blob = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(blob, ct);
            // Current key first, then the previous rotation generation:
            // a rotation must never make existing evidence unverifiable.
            return DecryptAndCompare(blob, _key, intentId, reviewerId, documentSha256) ||
                (_previousKey is not null &&
                 DecryptAndCompare(blob, _previousKey, intentId, reviewerId, documentSha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static bool DecryptAndCompare(byte[] blob, byte[] key,
        Guid intentId, Guid reviewerId, string documentSha256)
    {
        var plaintext = new byte[blob.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28),
                blob.AsSpan(12, 16), plaintext, Binding(intentId, reviewerId));
            var actual = SHA256.HashData(plaintext);
            byte[] expected;
            try { expected = Convert.FromHexString(documentSha256); }
            catch (FormatException) { return false; }
            if (expected.Length != 32) return false;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>
    /// P1.3 built-in file validation: only PNG, JPEG or PDF statements are
    /// accepted in this lane. Magic-byte check (no extension trust, no
    /// full parse; a production archive adds a real malware scanner).
    /// </summary>
    private static void RequireAllowedFileType(byte[] document)
    {
        static bool Png(byte[] d) =>
            d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E &&
            d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;
        static bool Jpeg(byte[] d) =>
            d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
        static bool Pdf(byte[] d) =>
            d.Length >= 5 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46;
        if (!Png(document) && !Jpeg(document) && !Pdf(document))
            throw new InvalidOperationException(
                "D04-B bank evidence must be a PNG, JPEG or PDF statement.");
    }

    private static byte[] Binding(Guid intent, Guid reviewer) =>
        Encoding.UTF8.GetBytes(intent.ToString("N") + ":" + reviewer.ToString("N"));
}
