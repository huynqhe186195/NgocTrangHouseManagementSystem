using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Run ONLY on the dedicated disposable PG16 test database with the
/// BM_PAYOS_EF_TEST_DB=1 gate. Never on customer or production databases.
/// </summary>
public class PayOsStagingWebhookInboxWriter_Tests
{
    private const string Key = "isolated-payOS-webhook-checksum";
    private static long _orderSequence = DateTime.UtcNow.Ticks;

    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Refusing to run outside disposable PG16 CI test database.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<PayOsPaymentIntent> SeedAsync(bool cancelled = false)
    {
        await using var db = Db();
        var order = Interlocked.Increment(ref _orderSequence);
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), order, 150000,
            DateTime.UtcNow.AddDays(1), paymentId: Guid.NewGuid());
        intent.AttachProviderLink("LINK-" + order);
        if (cancelled) intent.CancelUnpaid();
        db.PayOsPaymentIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent;
    }

    private static byte[] Signed(PayOsPaymentIntent intent,
        long? amount = null, string reference = "BANK-REF-ONE")
    {
        var actualAmount = amount ?? intent.AmountVnd;
        var source = "amount=" + actualAmount +
            "&code=00&currency=VND&orderCode=" + intent.OrderCode +
            "&paymentLinkId=" + intent.PaymentLinkId + "&reference=" + reference;
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            code = "00",
            success = true,
            data = new
            {
                amount = actualAmount, code = "00", currency = "VND",
                orderCode = intent.OrderCode, paymentLinkId = intent.PaymentLinkId,
                reference
            },
            signature
        }));
    }

    [Fact]
    public async Task First_matching_webhook_is_durable_and_exact_repeat_is_idempotent()
    {
        var intent = await SeedAsync();
        await using var db = Db();
        var writer = new PayOsStagingWebhookInboxWriter(db, Key);
        var raw = Signed(intent, reference: "BANK-" + intent.OrderCode);
        Assert.Equal(PayOsWebhookIngestResult.StoredPendingReconciliation,
            await writer.RecordAsync(raw));
        Assert.Equal(PayOsWebhookIngestResult.Duplicate,
            await writer.RecordAsync(raw));
        var record = await db.PayOsWebhookInbox.AsNoTracking().SingleAsync(
            x => x.PaymentIntentId == intent.Id);
        Assert.Equal(PayOsWebhookInboxStatus.PendingReconciliation, record.Status);
        Assert.Equal(intent.BuildingId, record.BuildingId);
        Assert.Equal(intent.OrderCode, record.OrderCode);
        Assert.Equal(64, record.PayloadSha256.Length);
        // No Payment or invoice receivable is created or completed here.
    }

    [Fact]
    public async Task Wrong_amount_or_wrong_signature_never_creates_inbox_rows()
    {
        var intent = await SeedAsync();
        await using var db = Db();
        var writer = new PayOsStagingWebhookInboxWriter(db, Key);
        Assert.Equal(PayOsWebhookIngestResult.Rejected,
            await writer.RecordAsync(Signed(intent, amount: 149999)));
        var signed = Signed(intent);
        signed[signed.Length - 2] ^= 1;
        Assert.Equal(PayOsWebhookIngestResult.Rejected,
            await writer.RecordAsync(signed));
        Assert.Equal(0, await db.PayOsWebhookInbox.CountAsync(x =>
            x.PaymentIntentId == intent.Id));
    }

    [Fact]
    public async Task Late_money_after_cancel_is_stored_for_manual_review_only()
    {
        var intent = await SeedAsync(cancelled: true);
        await using var db = Db();
        var writer = new PayOsStagingWebhookInboxWriter(db, Key);
        Assert.Equal(PayOsWebhookIngestResult.StoredManualReview,
            await writer.RecordAsync(Signed(intent, reference: "BANK-" + intent.OrderCode)));
        var record = await db.PayOsWebhookInbox.AsNoTracking().SingleAsync(
            x => x.PaymentIntentId == intent.Id);
        Assert.Equal(PayOsWebhookInboxStatus.RequiresManualReview, record.Status);
        var current = await db.PayOsPaymentIntents.AsNoTracking().SingleAsync(
            x => x.Id == intent.Id);
        Assert.Equal(PayOsPaymentIntentStatus.Cancelled, current.Status);
    }

    [Fact]
    public async Task Same_bank_reference_on_another_order_is_a_conflict_not_a_second_receipt()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using var db1 = Db();
        await using var db2 = Db();
        var commonReference = "BANK-SHARED-" + first.OrderCode;
        Assert.Equal(PayOsWebhookIngestResult.StoredPendingReconciliation,
            await new PayOsStagingWebhookInboxWriter(db1, Key)
                .RecordAsync(Signed(first, reference: commonReference)));
        Assert.Equal(PayOsWebhookIngestResult.Conflict,
            await new PayOsStagingWebhookInboxWriter(db2, Key)
                .RecordAsync(Signed(second, reference: commonReference)));
        Assert.Equal(1, await db2.PayOsWebhookInbox.AsNoTracking()
            .CountAsync(x => x.ProviderReference == commonReference));
    }

    [Fact]
    public async Task Two_concurrent_replays_store_only_one_inbox_record()
    {
        var intent = await SeedAsync();
        var raw = Signed(intent, reference: "BANK-CONCURRENT-" + intent.OrderCode);
        async Task<PayOsWebhookIngestResult> Record()
        {
            await using var db = Db();
            return await new PayOsStagingWebhookInboxWriter(db, Key).RecordAsync(raw);
        }

        var results = await Task.WhenAll(Task.Run(Record), Task.Run(Record));
        Assert.Contains(PayOsWebhookIngestResult.StoredPendingReconciliation, results);
        Assert.All(results, result =>
            Assert.True(result is PayOsWebhookIngestResult.StoredPendingReconciliation
                or PayOsWebhookIngestResult.Duplicate
                or PayOsWebhookIngestResult.ConcurrentConflictRetry));

        await using var verify = Db();
        Assert.Equal(1, await verify.PayOsWebhookInbox.CountAsync(
            x => x.PaymentIntentId == intent.Id));
    }
}
