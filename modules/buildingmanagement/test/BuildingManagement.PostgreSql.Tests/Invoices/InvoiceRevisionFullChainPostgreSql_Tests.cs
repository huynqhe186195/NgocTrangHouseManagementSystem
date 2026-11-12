using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Opt-in end-to-end EF migration rehearsal using ONE random, disposable
/// database created only on localhost:5432. It does not migrate the
/// existing integration or application database. The generated V2
/// migration stays in the user's LOCAL working tree until reviewed.
/// </summary>
[Collection("Phase2G3A7_RevisionFullChain")]
public sealed class InvoiceRevisionFullChainPostgreSql_Tests
{
    private const string OptInVariable = "BM_ALLOW_REVISION_FULL_CHAIN_REHEARSAL";
    private const string OptInValue = "CREATE_AND_DROP_RANDOM_LOCAL_REVISION_TEST_DATABASE";
    private const string PriorMigration =
        "20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots";
    private const string NewMigration =
        "20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger";
    private const string UpHash =
        "b5aabd722050d3db3f7cff2703d8a7b44f0380fd9711f40470da35bf1d9dcb24";
    private const string DownHash =
        "38a06bfbbf76b0cbd973a6fa70b2e779e73f1a45a9f61f12bbd7dda02bc4a416";

    [Fact]
    public async Task PGREV03_Full_Migration_Chain_Up_EmptyDown_Up_In_Disposable_Database()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue)
            throw new InvalidOperationException(
                "Opt-in scratch database migration rehearsal only. Use " +
                "etc/scripts/test-invoice-revision-full-chain-local.ps1.");

        // Both SQL exports are reviewed, checksum-pinned inputs.
        // Reject changed/unreviewed artifacts BEFORE connecting to PostgreSQL.
        VerifyApprovedSql(
            "BM_PHASE2G3A7_UP_SQL",
            "Phase2G3A7_RevisionLedger_Patched_Review.sql", UpHash);
        VerifyApprovedSql(
            "BM_PHASE2G3A7_DOWN_SQL",
            "Phase2G3A7_RevisionLedger_Down_Review.sql", DownHash);

        // Existing guard rejects all hosts but 127.0.0.1/localhost, all
        // databases but ngoctrang_integration_test and all ports but 5432.
        var allowed = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var adminOptions = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false,
            CommandTimeout = 90
        };

        // Probe the compiled EF migration assembly with NO DB connection.
        // This catches a missing/uncommitted local V2 migration before
        // attempting to create any scratch database.
        var probeOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
            .UseNpgsql(adminOptions.ConnectionString).Options;
        string[] migrations;
        await using (var probe = new BuildingManagementDbContext(probeOptions))
            migrations = probe.Database.GetMigrations().ToArray();

        Assert.NotEmpty(migrations);
        Assert.Contains(PriorMigration, migrations);
        Assert.Equal(NewMigration, migrations[^1]);

        await using var admin = new NpgsqlConnection(adminOptions.ConnectionString);
        await admin.OpenAsync();

        Assert.Equal(
            BuildingManagementPostgreSqlTestModule.DatabaseName,
            await TextAsync(admin, "SELECT current_database()"));

        // Explicit permission guard, BEFORE CREATE DATABASE.
        if (!await BoolAsync(admin,
                "SELECT rolcreatedb OR rolsuper " +
                "FROM pg_roles WHERE rolname = current_user"))
            throw new InvalidOperationException(
                "Safe stop: dedicated local PostgreSQL test role lacks CREATEDB. " +
                "No schema or database was modified.");

        var scratchName =
            "ngoctrang_revision_rehearsal_" + Guid.NewGuid().ToString("N");
        Assert.True(scratchName.Length < 63);
        var created = false;
        var stage = "Create random disposable local database";
        try
        {
            await ExecuteAsync(admin,
                $"CREATE DATABASE \"{scratchName}\" TEMPLATE template0");
            created = true;

            var scratchOptions = new NpgsqlConnectionStringBuilder(
                adminOptions.ConnectionString)
            {
                Database = scratchName,
                Pooling = false,
                CommandTimeout = 180
            };
            var dbOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(scratchOptions.ConnectionString).Options;

            stage = "Apply complete EF migration chain";
            await using (var db = new BuildingManagementDbContext(dbOptions))
            {
                Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
                await db.Database.MigrateAsync();
                Assert.Equal(migrations,
                    (await db.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());

                stage = "Verify repeating the full chain is idempotent";
                await db.Database.MigrateAsync();
                Assert.Equal(migrations,
                    (await db.Database.GetAppliedMigrationsAsync()).ToArray());

                stage = "Inspect actual PostgreSQL revision constraints and triggers";
                await using (var check = new NpgsqlConnection(
                    scratchOptions.ConnectionString))
                {
                    await check.OpenAsync();
                    await VerifyLedgerSchemaAsync(check);
                    Assert.Equal(1L, await ScalarLongAsync(check,
                        "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                        $"WHERE \"MigrationId\" = '{NewMigration}'"));
                }

                stage = "Downgrade only the empty revision ledger migration";
                await db.Database.MigrateAsync(PriorMigration);
                Assert.Equal(migrations.Length - 1,
                    (await db.Database.GetAppliedMigrationsAsync()).Count());
                Assert.Contains(PriorMigration,
                    await db.Database.GetAppliedMigrationsAsync());

                await using (var check = new NpgsqlConnection(
                    scratchOptions.ConnectionString))
                {
                    await check.OpenAsync();
                    Assert.Equal(0L, await ScalarLongAsync(check,
                        "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' " +
                        "AND tablename = 'BuildingManagementInvoiceDraftRevisions'"));
                    Assert.Equal(1L, await ScalarLongAsync(check,
                        "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' " +
                        "AND tablename = 'BuildingManagementInvoices'"));
                    Assert.Equal(0L, await ScalarLongAsync(check,
                        "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                        $"WHERE \"MigrationId\" = '{NewMigration}'"));
                }

                stage = "Upgrade revision ledger again after safe empty rollback";
                await db.Database.MigrateAsync();
                Assert.Equal(migrations,
                    (await db.Database.GetAppliedMigrationsAsync()).ToArray());

                await using (var check = new NpgsqlConnection(
                    scratchOptions.ConnectionString))
                {
                    await check.OpenAsync();
                    await VerifyLedgerSchemaAsync(check);
                }
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "PGREV03 failed during: " + stage +
                ". Scratch DB cleanup still runs; review the inner error.",
                error);
        }
        finally
        {
            if (created)
            {
                // No connection pooling, all EF/Npgsql connections disposed.
                // Never DROP anything except the just-created random name.
                try
                {
                    await ExecuteAsync(admin, $"DROP DATABASE \"{scratchName}\"");
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        "Safety cleanup failed for disposable scratch database " +
                        scratchName + ". Inspect it manually; do not touch any " +
                        "other database.", error);
                }
            }
        }

        Assert.Equal(0L, await ScalarLongAsync(admin,
            "SELECT count(*) FROM pg_database WHERE datname = '" +
            scratchName + "'"));
    }

    private static async Task VerifyLedgerSchemaAsync(NpgsqlConnection connection)
    {
        Assert.Equal(1L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' " +
            "AND tablename = 'BuildingManagementInvoiceDraftRevisions'"));

        Assert.Equal(1L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' " +
            "AND indexname = 'UX_BM_InvoiceDraftRevision_Invoice_Number' " +
            "AND indexdef ILIKE 'CREATE UNIQUE INDEX%'"));

        Assert.Equal(1L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_constraint c " +
            "JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "WHERE n.nspname = 'public' " +
            "AND t.relname = 'BuildingManagementInvoiceDraftRevisions' " +
            "AND c.contype = 'f' AND c.confdeltype = 'r'"));

        Assert.Equal(5L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_constraint c " +
            "JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "WHERE n.nspname = 'public' " +
            "AND t.relname = 'BuildingManagementInvoiceDraftRevisions' " +
            "AND c.contype = 'c' AND c.conname LIKE 'CK_BM_InvoiceDraftRevision_%'"));

        Assert.Equal(2L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_trigger g " +
            "JOIN pg_class t ON t.oid = g.tgrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "WHERE n.nspname = 'public' " +
            "AND t.relname = 'BuildingManagementInvoiceDraftRevisions' " +
            "AND NOT g.tgisinternal AND g.tgenabled = 'O'"));

        Assert.Equal(1L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_proc p " +
            "JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'public' " +
            "AND p.proname = 'bm_block_invoice_draft_revision_mutation'"));

        Assert.Equal(1L, await ScalarLongAsync(connection,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' " +
            "AND tablename = 'BuildingManagementInvoiceItemMeterSources'"));
    }

    private static void VerifyApprovedSql(
        string key, string filename, string expectedSha256)
    {
        var path = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), filename, StringComparison.Ordinal) ||
            !File.Exists(path))
            throw new InvalidOperationException(
                "Safety stop: expected approved local SQL file " + filename);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
        if (actual != expectedSha256)
            throw new InvalidOperationException(
                "Safety stop: unreviewed SQL change in " + filename);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> TextAsync(
        NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<bool> BoolAsync(
        NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync());
    }
}
