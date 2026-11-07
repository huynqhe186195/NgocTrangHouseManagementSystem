using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Real PostgreSQL staging-ACK tests with a fake ALREADY-COMMITTED legacy
/// reader. They do not simulate real bank authorization or financial posting.
/// Dedicated disposable PG16 staging tables only, no production database.
/// </summary>
public sealed class PayOsStagingD04StagingReceiptAcknowledger_Tests
{
    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException(
                "D04 staging ACK tests require dedicated disposable PostgreSQL.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static async Task<(PayOsPaymentIntent Intent, FakeCommittedReceiptStore Legacy)>
        SeedAsync(PayOsPaymentMigrationDbContext db)
    {
        var now = DateTime.UtcNow;
        var building = Guid.NewGuid();
        var payer = Guid.NewGuid();
        var payment = new Payment(
            Guid.NewGuid(), "ACK-TEST-" + Guid.NewGuid().ToString("N"),
            payer, PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, 150000m, contractId: Guid.NewGuid());
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), building, payer,
            RandomNumberGenerator.GetInt32(1, int.MaxValue),
            150000, now.AddDays(1), paymentId: payment.Id);
        intent.AttachProviderLink("LINK-" + Guid.NewGuid().ToString("N"));
        var bankRef = "BANK-" + Guid.NewGuid().ToString("N");
        intent.MarkConfirmed(bankRef, now);
        var inbox = new PayOsWebhookInboxRecord(
            Guid.NewGuid(), building, intent.Id, intent.OrderCode, bankRef,
            new string('a', 64), now);
        inbox.MarkReconciled(now);
        db.PayOsPaymentIntents.Add(intent);
        db.PayOsWebhookInbox.Add(inbox);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        payment.Complete(now, bankRef);
        var reviewer = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var key = PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(intent);
        var plan = new PayOsD04LegacyCommitPlan(
            intent.Id, building, payment.Id, intent.OrderCode, 150000, bankRef,
            key, new string('b', 64), reviewer, owner);
        var proof = new PayOsD04ReceiptOutboxProof(
            intent.Id, building, payment.Id, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), key, bankRef, 150000, 150000, new string('b', 64),
            owner, 1, 1, 1, 1, now, now);
        return (intent, new FakeCommittedReceiptStore(
            new PayOsD04CommittedReadBack(plan, proof, payment, false)));
    }

    [Fact]
    public async Task No_committed_legacy_journal_keeps_intent_pending_and_outbox_untouched()
    {
        await using var db = Db();
        var (intent, legacy) = await SeedAsync(db);
        legacy.Result = null;
        var coordinator = new PayOsD04StagingReceiptAcknowledger(db, legacy);

        Assert.Equal(PayOsD04AckOutcome.NoCommittedFinancialJournal,
            await coordinator.AcknowledgeAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            (await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(0, legacy.AckCalls);
    }

    [Fact]
    public async Task Exact_committed_journal_marks_posted_then_outbox_ack_and_replay_is_read_only()
    {
        await using var db = Db();
        var (intent, legacy) = await SeedAsync(db);
        var coordinator = new PayOsD04StagingReceiptAcknowledger(db, legacy);

        Assert.Equal(PayOsD04AckOutcome.PostedAndOutboxAcknowledged,
            await coordinator.AcknowledgeAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.Posted,
            (await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(1, legacy.AckCalls);

        Assert.Equal(PayOsD04AckOutcome.RecoveredAlreadyPosted,
            await coordinator.AcknowledgeAsync(intent.Id));
        Assert.Equal(2, legacy.AckCalls);
    }

    [Fact]
    public async Task Forged_legacy_proof_blocks_payos_posted_and_outbox_ack()
    {
        await using var db = Db();
        var (intent, legacy) = await SeedAsync(db);
        legacy.Result = legacy.Result! with {
            Proof = legacy.Result.Proof with { ExactSettlementCount = 0 }
        };
        var coordinator = new PayOsD04StagingReceiptAcknowledger(db, legacy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcknowledgeAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            (await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(0, legacy.AckCalls);
    }

    [Fact]
    public async Task Crash_after_posted_before_legacy_outbox_ack_is_retry_safe()
    {
        await using var db = Db();
        var (intent, legacy) = await SeedAsync(db);
        legacy.FailNextAck = true;
        var coordinator = new PayOsD04StagingReceiptAcknowledger(db, legacy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcknowledgeAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.Posted,
            (await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);

        Assert.Equal(PayOsD04AckOutcome.RecoveredAlreadyPosted,
            await coordinator.AcknowledgeAsync(intent.Id));
        Assert.Equal(2, legacy.AckCalls);
    }

    [Fact]
    public async Task Legacy_outbox_ack_before_staging_posted_is_conflict_not_permission_to_post()
    {
        await using var db = Db();
        var (intent, legacy) = await SeedAsync(db);
        legacy.Result = legacy.Result! with { OutboxAcknowledged = true };
        var coordinator = new PayOsD04StagingReceiptAcknowledger(db, legacy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcknowledgeAsync(intent.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting,
            (await db.PayOsPaymentIntents.SingleAsync(x => x.Id == intent.Id)).Status);
        Assert.Equal(0, legacy.AckCalls);
    }

    private sealed class FakeCommittedReceiptStore : IPayOsD04CommittedReceiptStore
    {
        public PayOsD04CommittedReadBack? Result { get; set; }
        public int AckCalls { get; private set; }
        public bool FailNextAck { get; set; }

        public FakeCommittedReceiptStore(PayOsD04CommittedReadBack result) => Result = result;

        public Task<PayOsD04CommittedReadBack?> ReadCommittedAsync(
            Guid intentId, CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task MarkOutboxAcknowledgedAsync(
            Guid outboxId, string sourceKey, CancellationToken cancellationToken)
        {
            AckCalls++;
            if (FailNextAck)
            {
                FailNextAck = false;
                throw new InvalidOperationException("Synthetic crash after staging commit.");
            }
            if (Result is null || Result.Proof.OutboxId != outboxId ||
                Result.Proof.SourceKey != sourceKey)
                throw new InvalidOperationException("Unexpected ACK proof.");
            Result = Result with { OutboxAcknowledged = true };
            return Task.CompletedTask;
        }
    }
}
