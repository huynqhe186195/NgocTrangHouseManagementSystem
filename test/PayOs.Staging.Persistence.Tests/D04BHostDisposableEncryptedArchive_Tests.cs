using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Exercises the archive class the Host actually registers for D04-B, with synthetic bytes and a scratch directory only.
/// </summary>
public sealed class D04BHostDisposableEncryptedArchive_Tests
{
    private const int EnvelopeHeaderBytes = 28;

    [Fact]
    public async Task D04B_HOSTARCHIVE01_Encrypts_At_Rest_And_Binds_Intent_Reviewer_And_Digest()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = SyntheticProof(1);
        try
        {
            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            var receipt = await archive.StoreAndVerifyAsync(intent, reviewer, proof);

            Assert.Equal(32, receipt.Reference.Length);
            Assert.True(Guid.TryParseExact(receipt.Reference, "N", out _));
            Assert.Equal(Sha(proof), receipt.DocumentSha256);
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            var file = Assert.Single(Directory.GetFiles(root));
            Assert.Equal(receipt.Reference + ".d04vault", Path.GetFileName(file));
            var disk = File.ReadAllBytes(file);
            Assert.Equal(EnvelopeHeaderBytes + proof.Length, disk.Length);
            Assert.False(ContainsBytes(disk, Encoding.ASCII.GetBytes("%PDF-1.7")));
            Assert.False(ContainsBytes(disk, Encoding.ASCII.GetBytes("SYNTHETIC ONLY")));

            Assert.False(await archive.VerifyAsync(
                intent, Guid.NewGuid(), receipt.Reference, receipt.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                Guid.NewGuid(), reviewer, receipt.Reference, receipt.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                reviewer, intent, receipt.Reference, receipt.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                Guid.Empty, reviewer, receipt.Reference, receipt.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                intent, Guid.Empty, receipt.Reference, receipt.DocumentSha256));

            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, Sha(SyntheticProof(2))));
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, new string('z', 64)));
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, "0f"));
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, null!));

            Assert.True(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE02_Reference_Is_Never_Treated_As_A_Path()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = SyntheticProof(1);
        try
        {
            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            var receipt = await archive.StoreAndVerifyAsync(intent, reviewer, proof);
            var stored = Path.Combine(root, receipt.Reference + ".d04vault");
            var dashed = Guid.ParseExact(receipt.Reference, "N").ToString("D");

            foreach (var reference in new[]
            {
                "../escape",
                "..\\escape",
                stored,
                receipt.Reference + ".d04vault",
                "./" + receipt.Reference[2..],
                receipt.Reference[..31],
                receipt.Reference + "0",
                dashed,
                "{" + receipt.Reference[..30] + "}",
                new string('.', 32),
                string.Empty
            })
            {
                Assert.False(await archive.VerifyAsync(
                    intent, reviewer, reference, receipt.DocumentSha256));
            }
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, null!, receipt.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, Guid.NewGuid().ToString("N"), receipt.DocumentSha256));

            Assert.True(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE03_Tampered_Truncated_Extended_Or_Missing_File_Fails_Closed()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = SyntheticProof(1);
        try
        {
            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            var receipt = await archive.StoreAndVerifyAsync(intent, reviewer, proof);
            var path = Path.Combine(root, receipt.Reference + ".d04vault");
            var original = File.ReadAllBytes(path);

            foreach (var index in new[] { 0, 11, 12, 27, 28, original.Length - 1 })
            {
                var flipped = (byte[])original.Clone();
                flipped[index] ^= 1;
                File.WriteAllBytes(path, flipped);
                Assert.False(await archive.VerifyAsync(
                    intent, reviewer, receipt.Reference, receipt.DocumentSha256));
            }

            foreach (var length in new[] { 0, 27, 283, original.Length - 1 })
            {
                File.WriteAllBytes(path, original.AsSpan(0, length).ToArray());
                Assert.False(await archive.VerifyAsync(
                    intent, reviewer, receipt.Reference, receipt.DocumentSha256));
            }

            var extended = new byte[original.Length + 1];
            original.CopyTo(extended, 0);
            File.WriteAllBytes(path, extended);
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            File.Delete(path);
            Assert.False(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            File.WriteAllBytes(path, original);
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE04_Swapped_Or_Replayed_Envelope_Cannot_Satisfy_Recorded_Proof()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intentA = Guid.NewGuid();
        var intentB = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        try
        {
            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            var first = await archive.StoreAndVerifyAsync(intentA, reviewer, SyntheticProof(1));
            var second = await archive.StoreAndVerifyAsync(intentB, reviewer, SyntheticProof(2));
            var third = await archive.StoreAndVerifyAsync(intentA, reviewer, SyntheticProof(3));
            var firstPath = Path.Combine(root, first.Reference + ".d04vault");
            var secondPath = Path.Combine(root, second.Reference + ".d04vault");
            var thirdPath = Path.Combine(root, third.Reference + ".d04vault");
            var firstBytes = File.ReadAllBytes(firstPath);

            // Envelope of another intent replayed under this intent's reference.
            File.WriteAllBytes(secondPath, firstBytes);
            Assert.False(await archive.VerifyAsync(
                intentB, reviewer, second.Reference, second.DocumentSha256));
            Assert.False(await archive.VerifyAsync(
                intentB, reviewer, second.Reference, first.DocumentSha256));

            // Same intent and reviewer, different document: the recorded digest must still reject it.
            File.WriteAllBytes(firstPath, File.ReadAllBytes(thirdPath));
            Assert.False(await archive.VerifyAsync(
                intentA, reviewer, first.Reference, first.DocumentSha256));

            File.WriteAllBytes(firstPath, firstBytes);
            Assert.True(await archive.VerifyAsync(
                intentA, reviewer, first.Reference, first.DocumentSha256));
            Assert.True(await archive.VerifyAsync(
                intentA, reviewer, third.Reference, third.DocumentSha256));
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE05_Survives_Restart_With_Same_Key_And_Denies_Wrong_Key()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var operatorKey = (byte[])key.Clone();
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = SyntheticProof(1);
        try
        {
            var first = new PayOsD04DisposableEncryptedArchive(root, key);
            // The Host module wipes its own key buffer right after registration.
            CryptographicOperations.ZeroMemory(key);
            var receipt = await first.StoreAndVerifyAsync(intent, reviewer, proof);

            var restarted = new PayOsD04DisposableEncryptedArchive(root, operatorKey);
            Assert.True(await restarted.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            var rekeyed = new PayOsD04DisposableEncryptedArchive(root, wrongKey);
            Assert.False(await rekeyed.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            var zeroKeyed = new PayOsD04DisposableEncryptedArchive(root, new byte[32]);
            Assert.False(await zeroKeyed.VerifyAsync(
                intent, reviewer, receipt.Reference, receipt.DocumentSha256));

            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(operatorKey);
            CryptographicOperations.ZeroMemory(wrongKey);
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE06_Repeated_Store_Never_Overwrites_An_Existing_Envelope()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var proof = SyntheticProof(1);
        try
        {
            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            var first = await archive.StoreAndVerifyAsync(intent, reviewer, proof);
            var firstPath = Path.Combine(root, first.Reference + ".d04vault");
            var firstBytes = File.ReadAllBytes(firstPath);

            var second = await archive.StoreAndVerifyAsync(intent, reviewer, proof);

            Assert.NotEqual(first.Reference, second.Reference);
            Assert.Equal(first.DocumentSha256, second.DocumentSha256);
            Assert.Equal(2, Directory.GetFiles(root).Length);
            Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, first.Reference, first.DocumentSha256));
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, second.Reference, second.DocumentSha256));
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    [Fact]
    public async Task D04B_HOSTARCHIVE07_Rejects_Invalid_Key_Root_Actors_And_Document_Bounds()
    {
        var root = NewRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        var intent = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                new PayOsD04DisposableEncryptedArchive(root, null!));
            Assert.Throws<ArgumentException>(() =>
                new PayOsD04DisposableEncryptedArchive(root, new byte[31]));
            Assert.Throws<ArgumentException>(() =>
                new PayOsD04DisposableEncryptedArchive(root, new byte[33]));
            Assert.Throws<ArgumentException>(() =>
                new PayOsD04DisposableEncryptedArchive("relative-vault", key));
            Assert.Throws<ArgumentException>(() =>
                new PayOsD04DisposableEncryptedArchive(" ", key));
            Assert.False(Directory.Exists(root));

            var archive = new PayOsD04DisposableEncryptedArchive(root, key);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                archive.StoreAndVerifyAsync(Guid.Empty, reviewer, SyntheticProof(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                archive.StoreAndVerifyAsync(intent, Guid.Empty, SyntheticProof(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                archive.StoreAndVerifyAsync(intent, reviewer, null!));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                archive.StoreAndVerifyAsync(intent, reviewer, new byte[255]));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                archive.StoreAndVerifyAsync(intent, reviewer, new byte[5_242_881]));
            Assert.Empty(Directory.GetFiles(root));

            var smallest = new byte[256];
            var smallestReceipt = await archive.StoreAndVerifyAsync(intent, reviewer, smallest);
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, smallestReceipt.Reference, smallestReceipt.DocumentSha256));

            var largest = new byte[5_242_880];
            var largestReceipt = await archive.StoreAndVerifyAsync(intent, reviewer, largest);
            Assert.True(await archive.VerifyAsync(
                intent, reviewer, largestReceipt.Reference, largestReceipt.DocumentSha256));
            Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally
        {
            Cleanup(root, key);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(),
        "payos-d04-host-archive-disposable-" + Guid.NewGuid().ToString("N"));

    private static byte[] SyntheticProof(byte marker)
    {
        var proof = new byte[512];
        Encoding.ASCII.GetBytes("%PDF-1.7\nD04-B SYNTHETIC ONLY\n").CopyTo(proof, 0);
        proof[^1] = marker;
        return proof;
    }

    private static string Sha(byte[] document) =>
        Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();

    private static bool ContainsBytes(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private static void Cleanup(string root, byte[] key)
    {
        CryptographicOperations.ZeroMemory(key);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
