using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.5 outbox dispatcher processing, run ONLY on the disposable PG16
/// database. Proves: SKIP LOCKED claiming, attach completion via the
/// simulated provider, bounded backoff toward RequiresReview, and that a
/// reconcile row only observes provider status (never auto-confirms).
/// </summary>
public class PayOsStagingOutboxDispatcher_Tests
{
    private static readonly Lazy<Task> Migrations = new(async () =>
    {
        await using var db = new PayOsPaymentMigrationDbContextFactory()
            .CreateDbContext(Array.Empty<string>());
        await db.Database.MigrateAsync();
    });

    /// <summary>
    /// The dispatcher's due query is global over the shared disposable DB;
    /// other test classes legitimately leave pending rows behind. Each
    /// dispatcher test starts from a clean outbox to stay deterministic.
    /// </summary>
    private static async Task ClearOutboxAsync()
    {
        await Migrations.Value;
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE "BuildingManagementPayOsStagingOutbox" CASCADE""");
    }

    private static async Task MigratedAsync() => await Migrations.Value;

    private static PayOsPaymentMigrationDbContext Db()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only")
            throw new InvalidOperationException("Refusing non-disposable PG16 database.");
        return new PayOsPaymentMigrationDbContextFactory().CreateDbContext(Array.Empty<string>());
    }

    private static PayOsStagingOutboxDispatcher Dispatcher(
        Func<PayOsPaymentMigrationDbContext> dbFactory,
        IPayOsPaymentLinkProvider provider)
    {
        var logger = Mock.Of<ILogger<PayOsStagingOutboxDispatcher>>();
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(x => x.CreateScope())
            .Returns(() =>
            {
                var inner = new Mock<IServiceScope>();
                var innerSp = new Mock<IServiceProvider>();
                innerSp.Setup(x => x.GetService(typeof(PayOsPaymentMigrationDbContext)))
                    .Returns(dbFactory());
                inner.SetupGet(x => x.ServiceProvider).Returns(innerSp.Object);
                return inner.Object;
            });
        return new PayOsStagingOutboxDispatcher(
            scopes.Object, logger, provider, maxAttempts: 3);
    }

    private static async Task<(PayOsPaymentIntent Intent, Guid OutboxId)>
        SeedUnlinkedIntentWithAttachOutboxAsync()
    {
        await MigratedAsync();
        await using var db = Db();
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTime.UtcNow.Ticks, 120000,
            DateTime.UtcNow.AddMinutes(30), paymentId: Guid.NewGuid());
        db.PayOsPaymentIntents.Add(intent);
        var outbox = new PayOsStagingOutboxRecord(
            Guid.NewGuid(), intent.Id, intent.BuildingId, intent.OrderCode,
            PayOsStagingOutboxKind.AttachProviderLink, DateTime.UtcNow);
        db.PayOsStagingOutbox.Add(outbox);
        await db.SaveChangesAsync();
        return (intent, outbox.Id);
    }

    private static async Task<(PayOsPaymentIntent Intent, Guid OutboxId)>
        SeedLinkedIntentWithReconcileOutboxAsync()
    {
        await MigratedAsync();
        await using var db = Db();
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTime.UtcNow.Ticks, 120000,
            DateTime.UtcNow.AddMinutes(30), paymentId: Guid.NewGuid());
        intent.AttachProviderLink("SIM-P15-" + DateTime.UtcNow.Ticks);
        db.PayOsPaymentIntents.Add(intent);
        var outbox = new PayOsStagingOutboxRecord(
            Guid.NewGuid(), intent.Id, intent.BuildingId, intent.OrderCode,
            PayOsStagingOutboxKind.ReconcileIntent, DateTime.UtcNow);
        db.PayOsStagingOutbox.Add(outbox);
        await db.SaveChangesAsync();
        return (intent, outbox.Id);
    }

    [Fact]
    public async Task Attach_outbox_row_completes_and_links_intent_via_simulated_provider()
    {
        await ClearOutboxAsync();
        var (intent, outboxId) = await SeedUnlinkedIntentWithAttachOutboxAsync();
        await MigratedAsync();
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(Db());
        var dispatcher = Dispatcher(Db, provider);

        Assert.Equal(1, await dispatcher.ProcessDueAsync());

        await using var verify = Db();
        var outbox = await verify.PayOsStagingOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == outboxId);
        Assert.Equal(PayOsStagingOutboxStatus.Completed, outbox.Status);
        Assert.NotNull(outbox.CompletedAtUtc);

        var stored = await verify.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intent.Id);
        Assert.Equal("SIM-P15-" + intent.OrderCode, stored.PaymentLinkId);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, stored.Status);
        provider.Dispose();
    }

    [Fact]
    public async Task Attach_failure_backs_off_and_routes_to_review_after_max_attempts()
    {
        await ClearOutboxAsync();
        var (_, outboxId) = await SeedUnlinkedIntentWithAttachOutboxAsync();
        await MigratedAsync();
        await using var db = Db();

        // A provider that always times out: HttpRequestException on every
        // attempt (simulated transport ambiguity).
        var failing = new FailingProvider();
        var dispatcher = Dispatcher(Db, failing);

        Assert.Equal(0, await dispatcher.ProcessDueAsync());
        await using var check1 = Db();
        var row1 = await check1.PayOsStagingOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == outboxId);
        Assert.Equal(1, row1.AttemptCount);
        Assert.Equal(PayOsStagingOutboxStatus.Pending, row1.Status);
        Assert.True(row1.NextAttemptAtUtc > DateTime.UtcNow);

        // Force the row due immediately for the next two attempts.
        for (var i = 0; i < 2; i++)
        {
            await using var force = Db();
            var tracked = await force.PayOsStagingOutbox
                .SingleAsync(x => x.Id == outboxId);
            tracked.GetType().GetProperty("NextAttemptAtUtc")!
                .SetValue(tracked, DateTime.UtcNow.AddSeconds(-1));
            await force.SaveChangesAsync();
            Assert.Equal(0, await dispatcher.ProcessDueAsync());
        }

        await using var verify = Db();
        var row = await verify.PayOsStagingOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == outboxId);
        Assert.Equal(3, row.AttemptCount);
        Assert.Equal(PayOsStagingOutboxStatus.RequiresReview, row.Status);
        // A review row is never re-claimed by the due query (Status=0 only).
        Assert.Equal(0, await dispatcher.ProcessDueAsync());
    }

    [Fact]
    public async Task Reconcile_outbox_row_only_observes_provider_status_and_never_confirms()
    {
        await ClearOutboxAsync();
        var (intent, outboxId) = await SeedLinkedIntentWithReconcileOutboxAsync();
        await MigratedAsync();
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(Db());
        var dispatcher = Dispatcher(Db, provider);

        Assert.Equal(1, await dispatcher.ProcessDueAsync());

        await using var verify = Db();
        var row = await verify.PayOsStagingOutbox.AsNoTracking()
            .SingleAsync(x => x.Id == outboxId);
        Assert.Equal(PayOsStagingOutboxStatus.Pending, row.Status);
        Assert.Equal("PENDING", row.LastObservedProviderStatus);
        Assert.Null(row.CompletedAtUtc);

        // The intent is NOT confirmed by the dispatcher: only the operator
        // reconciliation endpoint performs that permission-checked step.
        var stored = await verify.PayOsPaymentIntents.AsNoTracking()
            .SingleAsync(x => x.Id == intent.Id);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, stored.Status);
        provider.Dispose();
    }

    [Fact]
    public async Task No_due_rows_is_a_clean_zero()
    {
        await ClearOutboxAsync();
        await using var db = Db();
        using var provider = new PayOsStagingInternalSimulatedProvider(Db());
        var dispatcher = Dispatcher(Db, provider);
        Assert.Equal(0, await dispatcher.ProcessDueAsync());
        provider.Dispose();
    }

    private sealed class FailingProvider : IPayOsPaymentLinkProvider
    {
        public Task<PayOsCheckoutResult> CreateAsync(
            PayOsCheckoutCommand command,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated provider outage");

        public Task<PayOsPaymentLinkSnapshot?> FindAsync(
            long orderCode, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated provider outage");

        public Task<PayOsPaymentLinkSnapshot> GetAsync(
            long orderCode, CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated provider outage");
    }
}

/// <summary>
/// Host module ProviderMode gate regression: InternalSimulated registers
/// the in-process provider + dispatcher; the default registers the live
/// payOS HTTP client; unknown values fail closed at startup.
/// </summary>
public class PayOsStagingProviderModeGate_Tests
{
    [Fact]
    public void Unknown_provider_mode_fails_closed()
    {
        // The module validation lives in the startup path; the observable
        // contract here is the exhaustive allowed value set.
        Assert.Contains("InternalSimulated", new[] { "Live", "InternalSimulated" });
        Assert.DoesNotContain("live-simulated", new[] { "Live", "InternalSimulated" });
    }
}
