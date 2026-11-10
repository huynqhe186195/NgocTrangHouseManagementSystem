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
/// </summary>
public sealed class PayOsD04DisposableEncryptedArchive : IPayOsD04ProtectedBankEvidenceArchive
{
    private readonly string _root;
    private readonly byte[] _key;

    public PayOsD04DisposableEncryptedArchive(string root, byte[] key)
    {
        if (key is null || key.Length != 32) throw new ArgumentException("AES-256 key required.", nameof(key));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Absolute archive directory required.", nameof(root));
        _root = Path.GetFullPath(root);
        _key = (byte[])key.Clone();
        Directory.CreateDirectory(_root);
    }

    public async Task<PayOsD04ProtectedBankEvidenceReceipt> StoreAndVerifyAsync(
        Guid intentId, Guid reviewerId, byte[] document, CancellationToken ct = default)
    {
        if (intentId == Guid.Empty || reviewerId == Guid.Empty ||
            document is null || document.Length is < 256 or > 5_242_880)
            throw new InvalidOperationException("Invalid D04-B synthetic evidence.");
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
            var plaintext = new byte[blob.Length - 28];
            try
            {
                using var aes = new AesGcm(_key, 16);
                aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28),
                    blob.AsSpan(12, 16), plaintext, Binding(intentId, reviewerId));
                var actual = SHA256.HashData(plaintext);
                if (!Convert.TryFromHexString(documentSha256, new byte[32], out var written) || written != 32)
                    return false;
                var expected = Convert.FromHexString(documentSha256);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Binding(Guid intent, Guid reviewer) =>
        Encoding.UTF8.GetBytes(intent.ToString("N") + ":" + reviewer.ToString("N"));
}
