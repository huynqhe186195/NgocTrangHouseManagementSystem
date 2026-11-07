using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

public sealed class PayOsStagingD04TrustedReader_Tests
{
    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException(
                "Only the dedicated disposable payOS PostgreSQL database is allowed.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<(PayOsPaymentIntent Intent, PayOsWebhookInboxRecord? Inbox)>
        SeedAsync(PayOsPaymentMigrationDbContext db, bool signedInbox,
            bool matchingReference = true, bool posted = false)
    {
        var now = DateTime.UtcNow;
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            RandomNumberGenerator.GetInt32(1, int.MaxValue),
            150000, now.AddDays(1), paymentId: Guid.NewGuid());
        intent.AttachProviderLink("LINK-" + Guid.NewGuid().ToString("N"));
        var reference = "BANK-" + Guid.NewGuid().ToString("N");
        intent.MarkConfirmed(reference, now);

        PayOsWebhookInboxRecord? inbox = null;
        if (signedInbox)
        {
            inbox = new PayOsWebhookInboxRecord(
                Guid.NewGuid(), intent.BuildingId, intent.Id, intent.OrderCode,
                matchingReference ? reference : "OTHER-" + Guid.NewGuid().ToString("N"),
                new string('a', 64), now);
            inbox.MarkReconciled(now);
            db.PayOsWebhookInbox.Add(inbox);
        }

        if (posted) intent.MarkPosted();
        db.PayOsPaymentIntents.Add(intent);
        await db.SaveChangesAsync();
        return (intent, inbox);
    }

    [Fact]
    public async Task Real_persisted_intent_and_signed_reconciled_inbox_are_required()
    {
        await using var db = Db();
        var (intent, _) = await SeedAsync(db, signedInbox: true);
        db.ChangeTracker.Clear();

        var reader = new PayOsD04TrustedStagingEvidenceReader(db);
        var result = await reader.ReadCurrentAsync(intent.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(intent.Id, result.Value.Intent.Id);
        Assert.Equal(intent.BuildingId, result.Value.Inbox.BuildingId);
        Assert.Equal(intent.ProviderReference, result.Value.Inbox.ProviderReference);
    }

    [Fact]
    public async Task Provider_confirmation_without_signed_inbox_is_not_financial_evidence()
    {
        await using var db = Db();
        var (intent, _) = await SeedAsync(db, signedInbox: false);
        db.ChangeTracker.Clear();

        var reader = new PayOsD04TrustedStagingEvidenceReader(db);
        Assert.Null(await reader.ReadCurrentAsync(intent.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Mismatched_signed_bank_reference_is_rejected()
    {
        await using var db = Db();
        var (intent, _) = await SeedAsync(db, signedInbox: true,
            matchingReference: false);
        db.ChangeTracker.Clear();

        var reader = new PayOsD04TrustedStagingEvidenceReader(db);
        Assert.Null(await reader.ReadCurrentAsync(intent.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Already_posted_intent_can_be_read_for_exact_legacy_ack_replay()
    {
        await using var db = Db();
        var (intent, _) = await SeedAsync(db, signedInbox: true, posted: true);
        db.ChangeTracker.Clear();

        var reader = new PayOsD04TrustedStagingEvidenceReader(db);
        var source = await reader.ReadCurrentAsync(intent.Id, CancellationToken.None);
        Assert.NotNull(source);
        Assert.Equal(PayOsPaymentIntentStatus.Posted, source.Value.Intent.Status);
    }
}
