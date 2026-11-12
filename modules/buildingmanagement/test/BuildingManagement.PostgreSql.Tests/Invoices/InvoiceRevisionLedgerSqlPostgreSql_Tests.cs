using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Executes the EXACT previously reviewed Up/Down SQL text in a
/// transaction-local random PostgreSQL schema. Both SQL files are
/// checksum-pinned. No EF Migrate(), EnsureCreated(), real invoice row,
/// existing schema change, new database or migration applied to public.
/// </summary>
public sealed class InvoiceRevisionLedgerSqlPostgreSql_Tests
{
    private const string UpFileName =
        "Phase2G3A7_RevisionLedger_Patched_Review.sql";
    private const string DownFileName =
        "Phase2G3A7_RevisionLedger_Down_Review.sql";
    private const string MigrationId =
        "20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger";

    // SHA-256 of the EXACT uploaded, statically reviewed files.
    // A changed SQL file requires a separate human review and a new
    // deliberate checksum update, NOT an automatic blind acceptance.
    private const string ApprovedUpHash =
        "b5aabd722050d3db3f7cff2703d8a7b44f0380fd9711f40470da35bf1d9dcb24";
    private const string ApprovedDownHash =
        "38a06bfbbf76b0cbd973a6fa70b2e779e73f1a45a9f61f12bbd7dda02bc4a416";

    [Fact]
    public async Task PGREV01_Populated_Ledger_Rejects_Mutations_Bad_Data_And_Rollback()
    {
        await InRolledBackScratchSchemaAsync(async (connection, tx, up, down) =>
        {
            var invoiceId = Guid.NewGuid();
            await ExecuteAsync(connection, tx,
                $"INSERT INTO \"BuildingManagementInvoices\" (\"Id\") VALUES ('{invoiceId:D}')");

            var evidence = MakeEvidence(invoiceId);
            await InsertValidAsync(connection, tx, evidence);

            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoiceDraftRevisions\""));
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                $"WHERE \"MigrationId\" = '{MigrationId}'"));

            // Constraint and trigger protection is verified from PostgreSQL
            // SQLSTATE, not by assuming the generated migration has it.
            await ExpectFailureAsync(connection, tx,
                "UPDATE \"BuildingManagementInvoiceDraftRevisions\" " +
                "SET \"Reason\" = 'Changed by attacker'", "P0001");
            await ExpectFailureAsync(connection, tx,
                "DELETE FROM \"BuildingManagementInvoiceDraftRevisions\"", "P0001");
            await ExpectFailureAsync(connection, tx,
                "TRUNCATE TABLE \"BuildingManagementInvoiceDraftRevisions\"", "P0001");

            var otherRecord = Guid.NewGuid();
            var archived = InvoiceDraftRevisionArchiveCodec.Pack(evidence);
            await ExpectFailureAsync(connection, tx,
                InsertSql(otherRecord, evidence.InvoiceId, 1,
                    archived.EvidenceJson, archived.EvidenceSha256), "23505");
            await ExpectFailureAsync(connection, tx,
                InsertSql(Guid.NewGuid(), invoiceId, 2,
                    archived.EvidenceJson, new string('g', 64)), "23514");
            await ExpectFailureAsync(connection, tx,
                InsertSql(Guid.NewGuid(), Guid.NewGuid(), 2,
                    archived.EvidenceJson, archived.EvidenceSha256), "23503");
            await ExpectFailureAsync(connection, tx,
                InsertSql(Guid.NewGuid(), invoiceId, 0,
                    archived.EvidenceJson, archived.EvidenceSha256), "23514");

            // Exact DOWN SQL must raise BEFORE dropping a nonempty ledger.
            await ExpectFailureAsync(connection, tx, down, "P0001");
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoiceDraftRevisions\""));
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                $"WHERE \"MigrationId\" = '{MigrationId}'"));
            Assert.Equal(2L, await CountAsync(connection, tx,
                "SELECT count(*) FROM pg_trigger " +
                "WHERE tgrelid = '\"BuildingManagementInvoiceDraftRevisions\"'::regclass " +
                "AND NOT tgisinternal AND tgenabled = 'O'"));
        });
    }

    [Fact]
    public async Task PGREV02_Empty_Ledger_Rollback_Removes_Only_Its_Own_Objects()
    {
        await InRolledBackScratchSchemaAsync(async (connection, tx, up, down) =>
        {
            Assert.Equal(0L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoiceDraftRevisions\""));

            await ExecuteAsync(connection, tx, down);

            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT CASE WHEN to_regclass('\"BuildingManagementInvoiceDraftRevisions\"') " +
                "IS NULL THEN 1 ELSE 0 END"));
            Assert.Equal(0L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                $"WHERE \"MigrationId\" = '{MigrationId}'"));
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM pg_tables " +
                "WHERE schemaname = current_schema() " +
                "AND tablename = 'BuildingManagementInvoices'"));
            Assert.Equal(0L, await CountAsync(connection, tx,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n " +
                "ON n.oid = p.pronamespace " +
                "WHERE n.nspname = current_schema() AND " +
                "p.proname = 'bm_block_invoice_draft_revision_mutation'"));
        });
    }

    private static InvoiceDraftRevisionEvidence MakeEvidence(Guid invoiceId)
    {
        InvoiceDraftUtilityLineSnapshot Line(UtilityType type, decimal price)
        {
            var source = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 25), 100m, 105m);
            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                5m, price, 5m * price, new[] { source }, Array.Empty<string>());
        }

        var old = InvoiceDraftSnapshot.Capture(
            Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
            new[] { Line(UtilityType.Electricity, 3500m),
                    Line(UtilityType.Water, 15000m) });
        return InvoiceDraftRevisionEvidence.Capture(
            invoiceId, 1, Guid.NewGuid(),
            new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            "Verified historical invoice correction audit", old);
    }

    private static string InsertSql(
        Guid id, Guid invoiceId, int revision, string json, string sha)
    {
        // Only locally generated UUIDs and test-produced JSON values enter
        // this test fixture. Escape the SQL text literal for data only.
        var safeJson = json.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            INSERT INTO "BuildingManagementInvoiceDraftRevisions"
            ("Id","InvoiceId","ArchivedRevision","ArchivedByUserId",
             "ArchivedAtUtc","Reason","EvidenceJson","EvidenceSha256")
            VALUES ('{id:D}','{invoiceId:D}',{revision},'{Guid.NewGuid():D}',
                    '2026-10-26T00:00:00Z',
                    'Verified historical invoice correction audit',
                    '{safeJson}','{sha}');
            """;
    }

    private static Task InsertValidAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        InvoiceDraftRevisionEvidence evidence)
    {
        var package = InvoiceDraftRevisionArchiveCodec.Pack(evidence);
        return ExecuteAsync(conn, tx,
            InsertSql(Guid.NewGuid(), evidence.InvoiceId,
                evidence.ArchivedRevision,
                package.EvidenceJson, package.EvidenceSha256));
    }

    private static async Task InRolledBackScratchSchemaAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, string, Task> test)
    {
        // Validate BOTH files before making any DB connection. Failure is
        // a safe stop; no automatic use of potentially unreviewed SQL.
        var up = LoadApprovedSql("BM_PHASE2G3A7_UP_SQL", UpFileName, ApprovedUpHash);
        var down = LoadApprovedSql("BM_PHASE2G3A7_DOWN_SQL", DownFileName, ApprovedDownHash);

        var allowed = BuildingManagementPostgreSqlTestModule
            .RequireSafeConnectionString(); // localhost:5432; integration test DB only
        var options = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false, CommandTimeout = 30
        };
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            connection.Database);

        var schema = "bm_rev_sql_" + Guid.NewGuid().ToString("N");
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(connection, transaction,
                    $"CREATE SCHEMA \"{schema}\"");
                // Do NOT include public in search_path: a missing stub
                // MUST NOT fall through to active integration data.
                await ExecuteAsync(connection, transaction,
                    $"SET LOCAL search_path TO \"{schema}\"");
                await ExecuteAsync(connection, transaction,
                    "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(connection, transaction,
                    "SET LOCAL statement_timeout = '20s'");
                Assert.Equal(schema, await ScalarStringAsync(connection, transaction,
                    "SELECT current_schema()"));

                // Only tiny test stubs needed by the actual reviewed SQL.
                // All objects are transaction-local and never committed.
                await ExecuteAsync(connection, transaction,
                    """
                    CREATE TABLE "BuildingManagementInvoices"
                       ("Id" uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE "__EFMigrationsHistory"
                       ("MigrationId" character varying(150) PRIMARY KEY,
                        "ProductVersion" character varying(32) NOT NULL);
                    """);

                await ExecuteAsync(connection, transaction, up);
                Assert.Equal(1L, await CountAsync(connection, transaction,
                    "SELECT count(*) FROM \"__EFMigrationsHistory\" " +
                    $"WHERE \"MigrationId\" = '{MigrationId}'"));
                Assert.Equal(2L, await CountAsync(connection, transaction,
                    "SELECT count(*) FROM pg_trigger " +
                    "WHERE tgrelid = '\"BuildingManagementInvoiceDraftRevisions\"'::regclass " +
                    "AND NOT tgisinternal"));

                await test(connection, transaction, up, down);
            }
            finally
            {
                // Roll back even after test assertion/SQL errors.
                // PostgreSQL transactional DDL removes schema, stubs,
                // trigger, function, history and archived row together.
                await transaction.RollbackAsync();
            }
        }
        Assert.Equal(0L, await CountAsync(connection, null,
            "SELECT count(*) FROM pg_namespace " +
            $"WHERE nspname = '{schema}'"));
    }

    private static string LoadApprovedSql(
        string variable, string filename, string expectedSha256)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), filename, StringComparison.Ordinal) ||
            !File.Exists(path))
            throw new InvalidOperationException(
                "Safety stop: reviewed SQL must be supplied as an absolute local file: " +
                filename);

        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (actual != expectedSha256)
            throw new InvalidOperationException(
                "Safety stop: SQL checksum changed; a new human review is required: " +
                filename);
        var sql = File.ReadAllText(path).TrimStart('\uFEFF');
        var match = Regex.Match(sql,
            @"\A\s*START TRANSACTION;\s*(.*?)\s*COMMIT;\s*\z",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidOperationException(
                "Safety stop: reviewed SQL must have one START/COMMIT wrapper.");
        var body = match.Groups[1].Value;
        if (filename == UpFileName &&
            (!body.Contains("TR_BM_InvoiceDraftRevision_ProtectTruncate",
                    StringComparison.Ordinal) ||
             !body.Contains("CK_BM_InvoiceDraftRevision_HexSha256",
                    StringComparison.Ordinal)))
            throw new InvalidOperationException("Safety stop: Up protections missing.");
        if (filename == DownFileName)
        {
            var lockAt = body.IndexOf(
                "LOCK TABLE \"BuildingManagementInvoiceDraftRevisions\"",
                StringComparison.Ordinal);
            var checkAt = body.IndexOf("IF EXISTS", StringComparison.Ordinal);
            var dropAt = body.IndexOf("DROP TABLE", StringComparison.Ordinal);
            if (lockAt < 0 || checkAt <= lockAt || dropAt <= checkAt)
                throw new InvalidOperationException(
                    "Safety stop: rollback lock/check/drop ordering is wrong.");
        }
        return body;
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, string expectedSqlState)
    {
        const string savepoint = "revision_probe";
        await ExecuteAsync(connection, tx, $"SAVEPOINT {savepoint}");
        string? state = null;
        try
        {
            await ExecuteAsync(connection, tx, sql);
        }
        catch (PostgresException ex)
        {
            state = ex.SqlState;
        }
        finally
        {
            await ExecuteAsync(connection, tx, $"ROLLBACK TO SAVEPOINT {savepoint}");
            await ExecuteAsync(connection, tx, $"RELEASE SAVEPOINT {savepoint}");
        }
        Assert.Equal(expectedSqlState, state);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return (string?)await command.ExecuteScalarAsync();
    }
}
