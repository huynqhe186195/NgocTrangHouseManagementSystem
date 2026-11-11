using System;
using System.IO;
using System.Reflection;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

/// <summary>
/// Protects the pre-H4 immutable completion fingerprint from accepting
/// H4 properties which the original (byte-preserved) hash does not cover.
/// This is a domain integrity check, not a substitute for SQL or bank proof.
/// </summary>
public sealed class RoomChangeMidMonthH4FingerprintBoundary_Tests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static RoomChangeMidMonthCompletionRecord Snapshot()
    {
        var type = typeof(RoomChangeMidMonthCompletionRecord);
        var ctor = type.GetConstructor(
            PrivateInstance, null, new[] { typeof(Guid) }, null);
        Assert.NotNull(ctor);
        var row = (RoomChangeMidMonthCompletionRecord)ctor!.Invoke(
            new object[] { Guid.NewGuid() });

        Set(row, nameof(row.RequestId), Guid.NewGuid());
        Set(row, nameof(row.SourceContractId), Guid.NewGuid());
        Set(row, nameof(row.TargetContractId), Guid.NewGuid());
        Set(row, nameof(row.SourceRoomId), Guid.NewGuid());
        Set(row, nameof(row.TargetRoomId), Guid.NewGuid());
        Set(row, nameof(row.PricingArchiveId), Guid.NewGuid());
        Set(row, nameof(row.ThreePartyArchiveId), Guid.NewGuid());
        Set(row, nameof(row.PricingRuleId), Guid.NewGuid());
        Set(row, nameof(row.SourceReceivableId), Guid.NewGuid());
        Set(row, nameof(row.SourceMonthlyInvoiceId), Guid.NewGuid());
        Set(row, nameof(row.CarriedDepositVnd), 3_000_000m);
        Set(row, nameof(row.TargetFirstMonthRentVnd), 1_500_000m);
        Set(row, nameof(row.SourceAdjustedReceivableVnd), 2_950_000m);
        Set(row, nameof(row.SourceReviewedReceiptsVnd), 2_950_000m);
        Set(row, nameof(row.CompletedAtUtc),
            new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc));
        Set(row, nameof(row.DispositionEvidenceReference),
            "ORIGINAL-SOURCE-ROOM-DISPOSITION-001");
        Set(row, nameof(row.CompletionReference),
            "ORIGINAL-SIGNED-TRANSFER-COMPLETION-001");
        RefreshHash(row);
        row.VerifyFingerprint();
        return row;
    }

    private static void Set(
        RoomChangeMidMonthCompletionRecord row, string name, object? value)
    {
        var field = typeof(RoomChangeMidMonthCompletionRecord).GetField(
            "<" + name + ">k__BackingField", PrivateInstance);
        Assert.NotNull(field);
        field!.SetValue(row, value);
    }

    private static void RefreshHash(RoomChangeMidMonthCompletionRecord row)
    {
        var hash = typeof(RoomChangeMidMonthCompletionRecord).GetMethod(
            "Hash", PrivateInstance);
        Assert.NotNull(hash);
        var digest = (string)hash!.Invoke(row, null)!;
        Set(row, nameof(row.EvidenceSha256), digest);
    }

    [Fact]
    public void TRFH4F01_Legacy_Without_Intent_Must_Reject_Every_Stray_H4_Field()
    {
        Action<RoomChangeMidMonthCompletionRecord>[] mutations =
        {
            row => Set(row, nameof(row.H4IntentSha256), new string('a', 64)),
            row => Set(row, nameof(row.H4OriginalHeldDepositVnd), 1m),
            row => Set(row, nameof(row.H4TargetRequiredDepositVnd), 1m),
            row => Set(row, nameof(row.H4UnpaidTopUpVnd), 1m),
            row => Set(row, nameof(row.H4TopUpDueDateVietnam),
                new DateOnly(2026, 12, 15)),
            row => Set(row, nameof(row.H4NextMonthRentCreditVnd), 1m),
            row => Set(row, nameof(row.H4ManualRefundLiabilityVnd), 1m)
        };

        foreach (var mutation in mutations)
        {
            var row = Snapshot();
            var originalSha = row.EvidenceSha256;
            mutation(row);
            // No-intent legacy SHA deliberately does NOT include these fields.
            Assert.Equal(originalSha, row.EvidenceSha256);
            Assert.Throws<InvalidDataException>(() => row.VerifyFingerprint());
        }
    }

    [Fact]
    public void TRFH4F02_Intent_Digest_Requires_Lowercase_Hex_Even_When_Outer_Hash_Is_Valid()
    {
        var row = Snapshot();
        Set(row, nameof(row.H4DispositionIntentId), Guid.NewGuid());
        Set(row, nameof(row.H4IntentSha256), new string('a', 64));
        Set(row, nameof(row.H4OriginalHeldDepositVnd), 3_000_000m);
        Set(row, nameof(row.H4TargetRequiredDepositVnd), 3_000_000m);
        RefreshHash(row);
        row.VerifyFingerprint();

        Set(row, nameof(row.H4IntentSha256), new string('z', 64));
        RefreshHash(row);
        Assert.Throws<InvalidDataException>(() => row.VerifyFingerprint());

        Set(row, nameof(row.H4IntentSha256), new string('A', 64));
        RefreshHash(row);
        Assert.Throws<InvalidDataException>(() => row.VerifyFingerprint());
    }

    [Fact]
    public void TRFH4F03_NoIntent_And_Zero_H4_Fields_Preserve_Legacy_Digest()
    {
        var row = Snapshot();
        var digest = row.EvidenceSha256;
        row.VerifyFingerprint();
        Assert.Null(row.H4DispositionIntentId);
        Assert.Null(row.H4IntentSha256);
        Assert.Equal(digest, row.EvidenceSha256);
    }
}
