using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.Payments;
using Npgsql;

// This is an actual separate OS process run ONLY under the guarded local
// disposable PostgreSQL 16 test script. It can never invoke a live payOS API
// or initiate any financial posting: read-only inspection + already-committed
// receipt acknowledgement ONLY. Both DB connections use role-restricted,
// non-superuser ACK credentials provisioned by the disposable 19a test.
if (Environment.GetEnvironmentVariable("BM_PAYOS_D04_REAL_WRITER_TEST_DB")
        != "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
    Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
    Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
        != "disposable-only" ||
    Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432" ||
    args.Length != 2 ||
    args[0] is not ("hold" or "recover" or "replay") ||
    !Guid.TryParse(args[1], out var intentId) || intentId == Guid.Empty)
{
    Console.Error.WriteLine("D04 PROBE SAFETY DENIAL");
    return 64;
}

const string buildingConnection =
    "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
    "Username=d04_ack_legacy_test;Password=disposable-only;Pooling=false;Timeout=5";
const string stagingConnection =
    "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
    "Username=d04_ack_staging_test;Password=disposable-only;Pooling=false;Timeout=5";

var legacyOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
    .UseNpgsql(buildingConnection).Options;
var stageOptions = new DbContextOptionsBuilder<PayOsPaymentMigrationDbContext>()
    .UseNpgsql(stagingConnection,
        db => db.MigrationsHistoryTable("__EFMigrationsHistory_PayOs")).Options;

await using var legacy = new BuildingManagementDbContext(legacyOptions);
await using var staging = new PayOsPaymentMigrationDbContext(stageOptions);
var receipts = new PayOsD04CommittedReceiptStore(legacy);
var proof = await receipts.ReadCommittedAsync(intentId, CancellationToken.None);
if (proof is null || proof.Proof.ReceiptId == Guid.Empty ||
    proof.Proof.OutboxId == Guid.Empty)
{
    Console.Error.WriteLine("D04 PROBE: durable receipt missing");
    return 65;
}

var intent = await staging.PayOsPaymentIntents.AsNoTracking()
    .SingleOrDefaultAsync(x => x.Id == intentId);
if (intent is null)
{
    Console.Error.WriteLine("D04 PROBE: persisted staging intent missing");
    return 66;
}

if (args[0] == "hold")
{
    if (proof.OutboxAcknowledged ||
        intent.Status != PayOsPaymentIntentStatus.ConfirmedPendingPosting)
    {
        Console.Error.WriteLine("D04 PROBE: expected post-commit/pre-ACK state");
        return 67;
    }

    // Parent forcibly terminates THIS OS PROCESS after this exact barrier.
    // Keep both DB contexts open while waiting. No in-memory state will
    // survive; the new process must recover via persisted journals only.
    Console.WriteLine("D04_PROCESS_BARRIER_COMMITTED_UNACKNOWLEDGED");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 68;
}

var acknowledger = new PayOsD04StagingReceiptAcknowledger(staging, receipts);
var outcome = await acknowledger.AcknowledgeAsync(intentId);
var expected = args[0] == "recover"
    ? PayOsD04AckOutcome.PostedAndOutboxAcknowledged
    : PayOsD04AckOutcome.RecoveredAlreadyPosted;

if (outcome != expected)
{
    Console.Error.WriteLine("D04 PROBE: ACK outcome mismatch " + outcome);
    return 69;
}

staging.ChangeTracker.Clear();
var durable = await staging.PayOsPaymentIntents.AsNoTracking()
    .SingleAsync(x => x.Id == intentId);
var finalProof = await receipts.ReadCommittedAsync(intentId, CancellationToken.None);
if (durable.Status != PayOsPaymentIntentStatus.Posted ||
    finalProof is null || !finalProof.OutboxAcknowledged ||
    finalProof.Proof.ReceiptId != proof.Proof.ReceiptId ||
    finalProof.Proof.OutboxId != proof.Proof.OutboxId)
{
    Console.Error.WriteLine("D04 PROBE: mismatch after restart and ACK");
    return 70;
}
Console.WriteLine(args[0] == "recover"
    ? "D04_FRESH_PROCESS_RECOVERY_PASS"
    : "D04_FRESH_PROCESS_REPLAY_READ_ONLY_PASS");
return 0;
