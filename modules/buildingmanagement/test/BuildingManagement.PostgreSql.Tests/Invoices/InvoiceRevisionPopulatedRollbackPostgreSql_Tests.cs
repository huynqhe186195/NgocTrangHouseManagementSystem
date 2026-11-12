using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// A real EF Down() on a populated historical ledger must fail without
/// deleting data, even while a second connection has an uncommitted
/// archive insert. All work happens in one freshly created, randomly
/// named, disposable localhost PostgreSQL database.
/// </summary>
[Collection("Phase2G3A7_RevisionFullChain")]
public sealed class InvoiceRevisionPopulatedRollbackPostgreSql_Tests
{
    private const string Prior =
        "20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots";
    private const string Latest =
        "20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger";
    private const string ApprovalFlag = "BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL";
    private const string ApprovalValue = "CREATE_AND_DROP_RANDOM_LOCAL_REVISION_TEST_DATABASE";

    [Fact]
    public async Task PGREV04_Concurrent_Insert_Blocks_Down_And_Preserves_History()
    {
        if (Environment.GetEnvironmentVariable(ApprovalFlag) != ApprovalValue)
            throw new InvalidOperationException("Use guarded local full-chain rehearsal script.");

        CheckSql("BM_PHASE2G3A7_UP_SQL",
            "Phase2G3A7_RevisionLedger_Patched_Review.sql",
            "b5aabd722050d3db3f7cff2703d8a7b44f0380fd9711f40470da35bf1d9dcb24");
        CheckSql("BM_PHASE2G3A7_DOWN_SQL",
            "Phase2G3A7_RevisionLedger_Down_Review.sql",
            "38a06bfbbf76b0cbd973a6fa70b2e779e73f1a45a9f61f12bbd7dda02bc4a416");

        var allowed = BuildingManagementPostgreSqlTestModule
            .RequireSafeConnectionString();
        var options = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false, CommandTimeout = 90
        };

        // Validate the user's locally generated V2 migration has been
        // compiled BEFORE connecting to PostgreSQL.
        var probeOpts = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(options.ConnectionString).Options;
        await using (var probe = new BuildingManagementDbContext(probeOpts))
        {
            var migrations = probe.Database.GetMigrations().ToArray();
            Assert.Equal(Latest, migrations[^1]);
            Assert.Contains(Prior, migrations);
        }

        await using var admin = new NpgsqlConnection(options.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            await StringAsync(admin, "SELECT current_database()"));
        Assert.True(await BoolAsync(admin,
            "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user"),
            "Safe stop: local test role must have CREATEDB.");

        var scratch = "ngoctrang_revision_race_" + Guid.NewGuid().ToString("N");
        Assert.True(scratch.Length < 63);
        var created = false;
        var stage = "Create scratch DB";
        try
        {
            await ExecAsync(admin, $"CREATE DATABASE \"{scratch}\" TEMPLATE template0");
            created = true;

            var tempOptions = new NpgsqlConnectionStringBuilder(options.ConnectionString)
            {
                Database = scratch,
                Pooling = false,
                CommandTimeout = 120
            };
            var efOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(tempOptions.ConnectionString).Options;

            stage = "Apply the full EF migration chain";
            await using (var db = new BuildingManagementDbContext(efOptions))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(Latest, (await db.Database.GetAppliedMigrationsAsync()).Last());
            }

            stage = "Seed synthetic FK parent and invoice records";
            var building = Guid.NewGuid();
            var floor = Guid.NewGuid();
            var room = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var invoice = Guid.NewGuid();
            // RoomNumber is varchar(32); REV- plus an N-format UUID
            // would be 36 characters and fail SQLSTATE 22001.
            var roomNumber = "REV-" + Guid.NewGuid().ToString("N")[..8];
            Assert.InRange(roomNumber.Length, 1, 32);
            await using (var seed = new NpgsqlConnection(tempOptions.ConnectionString))
            {
                await seed.OpenAsync();

                // These are synthetic fixture identities on a brand-new
                // disposable database, never application tenant/room data.
                await ExecAsync(seed,
                    $$"""
                    INSERT INTO "Buildings"
                      ("Id","Name","Address","ExtraProperties","ConcurrencyStamp","CreationTime")
                    VALUES ('{{building:D}}','Revision test building','Test only',
                            '{}','{{Guid.NewGuid():N}}',now());
                    INSERT INTO "BuildingManagementFloors"
                      ("Id","BuildingId","FloorNumber","Name","ExtraProperties",
                       "ConcurrencyStamp","CreationTime")
                    VALUES ('{{floor:D}}','{{building:D}}',1,'Test floor','{}',
                            '{{Guid.NewGuid():N}}',now());
                    INSERT INTO "BuildingManagementRooms"
                      ("Id","FloorId","RoomNumber","Name","Capacity","Status",
                       "ExtraProperties","ConcurrencyStamp","CreationTime")
                    VALUES ('{{room:D}}','{{floor:D}}','{{roomNumber}}',
                            'Test room',2,0,'{}','{{Guid.NewGuid():N}}',now());
                    INSERT INTO "BuildingManagementContracts"
                      ("Id","RoomId","ContractNumber","StartDate","MonthlyRent",
                       "DepositAmount","Status","ExtraProperties",
                       "ConcurrencyStamp","CreationTime")
                    VALUES ('{{contract:D}}','{{room:D}}','REV-{{Guid.NewGuid():N}}',
                            now(),3000000,3000000,0,'{}',
                            '{{Guid.NewGuid():N}}',now());
                    INSERT INTO "BuildingManagementInvoices"
                      ("Id","ContractId","RoomId","BillingYear","BillingMonth",
                       "Status","ExtraProperties","ConcurrencyStamp","CreationTime")
                    VALUES ('{{invoice:D}}','{{contract:D}}','{{room:D}}',2026,10,
                            0,'{}','{{Guid.NewGuid():N}}',now());
                    """);
            }

            stage = "Hold uncommitted archive INSERT while EF tries to downgrade";
            await using (var holder = new NpgsqlConnection(tempOptions.ConnectionString))
            {
                await holder.OpenAsync();
                await using var insertTx = await holder.BeginTransactionAsync();
                var evidence = MakeEvidence(invoice, contract, room);
                var package = InvoiceDraftRevisionArchiveCodec.Pack(evidence);

                await using (var insert = new NpgsqlCommand(
                    """
                    INSERT INTO "BuildingManagementInvoiceDraftRevisions"
                      ("Id","InvoiceId","ArchivedRevision","ArchivedByUserId",
                       "ArchivedAtUtc","Reason","EvidenceJson","EvidenceSha256")
                    VALUES (@id,@invoice,@rev,@actor,@at,@reason,@json,@digest)
                    """, holder, insertTx))
                {
                    insert.Parameters.AddWithValue("id", Guid.NewGuid());
                    insert.Parameters.AddWithValue("invoice", invoice);
                    insert.Parameters.AddWithValue("rev", evidence.ArchivedRevision);
                    insert.Parameters.AddWithValue("actor", evidence.ArchivedByUserId);
                    insert.Parameters.AddWithValue("at", evidence.ArchivedAtUtc);
                    insert.Parameters.AddWithValue("reason", evidence.Reason);
                    insert.Parameters.AddWithValue("json", package.EvidenceJson);
                    insert.Parameters.AddWithValue("digest", package.EvidenceSha256);
                    await insert.ExecuteNonQueryAsync();
                }

                await using (var downgrade = new BuildingManagementDbContext(efOptions))
                {
                    await downgrade.Database.OpenConnectionAsync();
                    var rollbackConn =
                        (NpgsqlConnection)downgrade.Database.GetDbConnection();
                    var pid = await LongAsync(rollbackConn, "SELECT pg_backend_pid()");

                    // A holds ROW EXCLUSIVE on the new ledger. The V2 Down()
                    // must request ACCESS EXCLUSIVE before testing emptiness.
                    // Use pg_stat_activity to prove B really waits for a lock.
                    var downgradeTask = downgrade.Database.MigrateAsync(Prior);
                    var sawLockWait = false;
                    for (var i = 0; i < 100; i++)
                    {
                        if (downgradeTask.IsCompleted)
                            break;

                        if (await LongAsync(admin,
                            "SELECT count(*) FROM pg_stat_activity " +
                            $"WHERE pid = {pid} AND wait_event_type = 'Lock'") > 0)
                        {
                            sawLockWait = true;
                            break;
                        }
                        await Task.Delay(100);
                    }

                    // Release A even if lock observation did not succeed,
                    // so B can finish rather than deadlocking cleanup.
                    await insertTx.CommitAsync();
                    var failure = await Record.ExceptionAsync(
                        async () => await downgradeTask);
                    Assert.True(sawLockWait,
                        "Down() must wait for the concurrent uncommitted archive insert.");
                    Assert.NotNull(failure);
                    Assert.Contains(
                        "Cannot roll back draft revision ledger: archived history exists",
                        failure.ToString(), StringComparison.Ordinal);
                }
            }

            stage = "Verify committed history, triggers and EF migration remain";
            await using (var check = new NpgsqlConnection(tempOptions.ConnectionString))
            {
                await check.OpenAsync();
                Assert.Equal(1L, await LongAsync(check,
                    "SELECT count(*) FROM \"BuildingManagementInvoiceDraftRevisions\" " +
                    $"WHERE \"InvoiceId\" = '{invoice:D}'"));
                Assert.Equal(1L, await LongAsync(check,
                    "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                    $"WHERE \"MigrationId\" = '{Latest}'"));
                Assert.Equal(2L, await LongAsync(check,
                    "SELECT count(*) FROM pg_trigger " +
                    "WHERE tgrelid = '\"BuildingManagementInvoiceDraftRevisions\"'::regclass " +
                    "AND NOT tgisinternal AND tgenabled = 'O'"));
                Assert.Equal(1L, await LongAsync(check,
                    "SELECT count(*) FROM pg_tables WHERE schemaname='public' " +
                    "AND tablename='BuildingManagementInvoiceItemMeterSources'"));
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "PGREV04 failed at " + stage +
                ". Scratch database cleanup still follows; inspect inner error.", e);
        }
        finally
        {
            if (created)
            {
                // Drop ONLY a database created in this test. If cleanup
                // fails, report the name; do not terminate other connections
                // or drop/modify any existing integration/application DB.
                try
                {
                    await ExecAsync(admin, $"DROP DATABASE \"{scratch}\"");
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "PGREV04 scratch cleanup failed for " + scratch +
                        ". Inspect this test-created DB only.", e);
                }
            }
        }
        Assert.Equal(0L, await LongAsync(admin,
            $"SELECT count(*) FROM pg_database WHERE datname = '{scratch}'"));
    }

    private static InvoiceDraftRevisionEvidence MakeEvidence(
        Guid invoiceId, Guid contractId, Guid roomId)
    {
        InvoiceDraftUtilityLineSnapshot Line(UtilityType type, decimal price)
        {
            var segment = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 26), 100m, 105m);
            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                5m, price, 5m * price, new[] { segment },
                Array.Empty<string>());
        }
        var snapshot = InvoiceDraftSnapshot.Capture(
            contractId, roomId, 2026, 10,
            new[]
            {
                Line(UtilityType.Electricity, 3500m),
                Line(UtilityType.Water, 15000m)
            });
        return InvoiceDraftRevisionEvidence.Capture(
            invoiceId, 1, Guid.NewGuid(),
            new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            "Verified operator correction with archived old readings", snapshot);
    }

    private static void CheckSql(
        string variable, string filename, string requiredDigest)
    {
        var file = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(file) ||
            !Path.IsPathFullyQualified(file) ||
            !string.Equals(Path.GetFileName(file), filename, StringComparison.Ordinal) ||
            !File.Exists(file))
            throw new InvalidOperationException("Missing reviewed SQL: " + filename);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
            .ToLowerInvariant();
        if (sha != requiredDigest)
            throw new InvalidOperationException("Changed SQL; review required: " + filename);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> LongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> StringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task<bool> BoolAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync());
    }
}
