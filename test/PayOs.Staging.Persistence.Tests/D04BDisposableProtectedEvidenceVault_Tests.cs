using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// A disposable, test-only AES-256-GCM archive rehearsal. No production DI,
/// endpoint, configured encryption key or bank account. This deliberately
/// does NOT prove operational ACLs, off-host key management, malware scanning,
/// backup/restore, retention/deletion, or independent bank settlement.
///
/// The bank reviewer/intent and file hash are authenticated as AES-GCM AAD.
/// A secret-keyed filename prevents path traversal and stops callers from
/// choosing a storage path. Files are immutable, atomically renamed inside
/// one scratch directory, and verified on duplicate submission/readback.
/// </summary>
internal sealed class D04BDisposableProtectedEvidenceVault :
    IPayOsD04ProtectedBankEvidenceArchive, IDisposable
{
    private const string Prefix = "d04vault1:";
    private const int TagLength = 16;
    private const int NonceLength = 12;
    private const int MaxDocumentBytes = 5_242_880;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("D04BENC1");
    private readonly byte[] _key;
    private readonly string _root;
    private bool _disposed;

    internal sealed record Receipt(string Reference, string DocumentSha256);

    public D04BDisposableProtectedEvidenceVault(string scratchDirectory, byte[] key)
    {
        if (!Path.IsPathFullyQualified(scratchDirectory))
            throw new ArgumentException("Only a fully qualified scratch path is allowed.",
                nameof(scratchDirectory));
        if (key is null || key.Length != 32)
            throw new ArgumentException("AES-256 requires a separate 32-byte key.",
                nameof(key));

        _root = Path.GetFullPath(scratchDirectory);
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Reparse-point vault root forbidden.");
        _key = (byte[])key.Clone();
    }

    public Receipt Write(Guid intentId, Guid reviewerId, byte[] document)
    {
        ThrowIfDisposed();
        if (intentId == Guid.Empty || reviewerId == Guid.Empty ||
            document is null || document.Length is < 256 or > MaxDocumentBytes)
            throw new InvalidDataException("Invalid isolated evidence proof.");
        var hash = SHA256.HashData(document);
        var aad = BoundMetadata(intentId, reviewerId, hash);
        var reference = FilenameReference(aad);
        var destination = Path.Combine(_root, reference[Prefix.Length..] + ".vault");

        if (File.Exists(destination))
        {
            var old = Read(intentId, reviewerId, reference,
                Convert.ToHexString(hash).ToLowerInvariant());
            if (!CryptographicOperations.FixedTimeEquals(old, document))
                throw new InvalidDataException("Immutable proof conflict.");
            return new Receipt(reference, Convert.ToHexString(hash).ToLowerInvariant());
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[document.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(_key, TagLength))
            aes.Encrypt(nonce, document, ciphertext, tag, aad);

        var temp = Path.Combine(_root, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                }))
            {
                stream.Write(Magic);
                stream.Write(nonce);
                stream.Write(tag);
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }

            try { File.Move(temp, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                // A concurrent identical submission wins; it MUST decrypt
                // and match the exact hash/bytes, never overwrite an archive.
                var old = Read(intentId, reviewerId, reference,
                    Convert.ToHexString(hash).ToLowerInvariant());
                if (!CryptographicOperations.FixedTimeEquals(old, document))
                    throw new InvalidDataException("Immutable proof conflict.");
            }

            return new Receipt(reference,
                Convert.ToHexString(hash).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public byte[] Read(Guid intentId, Guid reviewerId,
        string reference, string documentSha256)
    {
        ThrowIfDisposed();
        if (intentId == Guid.Empty || reviewerId == Guid.Empty ||
            documentSha256 is null || documentSha256.Length != 64)
            throw new InvalidDataException("Invalid reference digest.");

        // Convert.TryFromHexString does not exist in the .NET 10 API used
        // by this project. Decode once with the supported FromHexString
        // method; malformed input fails closed with a stable exception.
        byte[] hash;
        try
        {
            hash = Convert.FromHexString(documentSha256);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid reference digest.", error);
        }
        var aad = BoundMetadata(intentId, reviewerId, hash);
        var expected = FilenameReference(aad);
        if (!string.Equals(reference, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Archive reference not bound to reviewer/intent.");
        var filename = Path.Combine(_root, expected[Prefix.Length..] + ".vault");
        var headerLength = Magic.Length + NonceLength + TagLength;
        // Check metadata before allocating a byte array: a malicious file
        // must not force an unbounded allocation. Production also requires
        // protected directory ACLs and no filesystem/link tampering.
        var info = new FileInfo(filename);
        if (info.Length < headerLength + 256 ||
            info.Length > headerLength + MaxDocumentBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid encrypted archive envelope.");
        var record = File.ReadAllBytes(filename);
        if (record.Length < headerLength + 256 ||
            record.Length > headerLength + MaxDocumentBytes ||
            !record.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid encrypted archive envelope.");
        var nonce = record.AsSpan(Magic.Length, NonceLength);
        var tag = record.AsSpan(Magic.Length + NonceLength, TagLength);
        var ciphertext = record.AsSpan(headerLength);
        var document = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, document, aad);
            var foundHash = SHA256.HashData(document);
            if (!CryptographicOperations.FixedTimeEquals(hash, foundHash))
                throw new InvalidDataException("Decrypted proof hash mismatch.");
            return document;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(document);
            throw;
        }
    }

    // This is a local test adapter for the staging-only archive facade.
    // A real production archive must implement durability, access control
    // and managed key rotation separately; no Host DI binds this vault.
    public Task<PayOsD04ProtectedBankEvidenceReceipt> StoreAndVerifyAsync(
        Guid intentId, Guid reviewerId, byte[] document,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var receipt = Write(intentId, reviewerId, document);
        var stored = Read(intentId, reviewerId, receipt.Reference,
            receipt.DocumentSha256);
        if (!CryptographicOperations.FixedTimeEquals(stored, document))
            throw new InvalidDataException("Archive readback bytes mismatch.");
        CryptographicOperations.ZeroMemory(stored);
        return Task.FromResult(new PayOsD04ProtectedBankEvidenceReceipt(
            receipt.Reference, receipt.DocumentSha256));
    }

    public Task<bool> VerifyAsync(
        Guid intentId, Guid reviewerId,
        string reference, string documentSha256,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var stored = Read(intentId, reviewerId, reference, documentSha256);
            CryptographicOperations.ZeroMemory(stored);
            return Task.FromResult(true);
        }
        catch (Exception error) when (
            error is IOException or InvalidDataException or
            UnauthorizedAccessException or CryptographicException or
            ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    private byte[] BoundMetadata(Guid intentId, Guid reviewerId, byte[] hash)
    {
        var aad = new byte[64];
        intentId.TryWriteBytes(aad.AsSpan(0, 16));
        reviewerId.TryWriteBytes(aad.AsSpan(16, 16));
        hash.CopyTo(aad, 32);
        return aad;
    }

    private string FilenameReference(byte[] aad) =>
        Prefix + Convert.ToHexString(HMACSHA256.HashData(_key, aad))
            .ToLowerInvariant();

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(
            D04BDisposableProtectedEvidenceVault));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}

public sealed class D04BDisposableProtectedEvidenceVault_Tests
{
    [Fact]
    public void D04B_ARCHIVE01_Encrypted_Immutable_ReviewerScoped_And_TamperDetecting()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "payos-d04-vault-disposable-" + Guid.NewGuid().ToString("N"));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = new byte[512];
        Encoding.ASCII.GetBytes("%PDF-1.7\nD04-B SYNTHETIC ONLY\n")
            .CopyTo(proof, 0);
        try
        {
            using var vault = new D04BDisposableProtectedEvidenceVault(root, key);
            var receipt = vault.Write(intent, reviewer, proof);
            Assert.StartsWith("d04vault1:", receipt.Reference);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(proof))
                .ToLowerInvariant(), receipt.DocumentSha256);
            Assert.Equal(proof, vault.Read(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));
            Assert.Equal(receipt, vault.Write(intent, reviewer, proof));

            var file = Path.Combine(root,
                receipt.Reference["d04vault1:".Length..] + ".vault");
            var disk = File.ReadAllBytes(file);
            Assert.False(disk.AsSpan().IndexOf(
                Encoding.ASCII.GetBytes("%PDF-1.7")) >= 0);
            Assert.Throws<InvalidDataException>(() =>
                vault.Read(intent, Guid.NewGuid(),
                    receipt.Reference, receipt.DocumentSha256));
            Assert.Throws<InvalidDataException>(() =>
                vault.Read(Guid.NewGuid(), reviewer,
                    receipt.Reference, receipt.DocumentSha256));
            Assert.Throws<InvalidDataException>(() =>
                vault.Read(intent, reviewer, "../escape", receipt.DocumentSha256));
            Assert.Throws<InvalidDataException>(() =>
                vault.Read(intent, reviewer, receipt.Reference,
                    new string('z', 64)));
            Assert.Throws<InvalidDataException>(() =>
                vault.Read(intent, reviewer, receipt.Reference, "0f"));

            disk[^1] ^= 1;
            File.WriteAllBytes(file, disk);
            Assert.Throws<CryptographicException>(() =>
                vault.Read(intent, reviewer,
                    receipt.Reference, receipt.DocumentSha256));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void D04B_ARCHIVE02_Missing_Persistent_Key_Fails_Closed()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "payos-d04-vault-disposable-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var doc = new byte[512];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(doc, 0);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            D04BDisposableProtectedEvidenceVault.Receipt receipt;
            using (var first = new D04BDisposableProtectedEvidenceVault(root, key))
                receipt = first.Write(id, reviewer, doc);

            using (var wrongKey = new D04BDisposableProtectedEvidenceVault(
                root, RandomNumberGenerator.GetBytes(32)))
                Assert.Throws<InvalidDataException>(() =>
                    wrongKey.Read(id, reviewer,
                        receipt.Reference, receipt.DocumentSha256));

            using var restored = new D04BDisposableProtectedEvidenceVault(root, key);
            Assert.Equal(doc, restored.Read(
                id, reviewer, receipt.Reference, receipt.DocumentSha256));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
