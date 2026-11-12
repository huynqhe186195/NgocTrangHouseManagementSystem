using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real migration Up/Down SQL, with actual predecessor evidence migration,
/// inside a fresh transaction-local random scratch schema. No application
/// DB, production credentials, Migrate(), EnsureCreated() or committed DDL.
/// </summary>
public sealed class SliceBIssuedInvoiceLedgerPostgreSql_Tests
{
    private sealed record TestInvoice(
        Guid Draft, Guid Request, Guid Evidence, Guid Review,
        Guid Contract, Guid Room, Guid Building,
        Guid Submitter, Guid Reviewer, string SourceSha);

    [Fact]
    public async Task SBLEDGER01_Valid_Provenance_Is_Immutable_And_Double_Issue_Is_Denied()
    {
        await InScratchAsync(async (connection, tx, down) =>
        {
            var f = await SeedReviewedInvoiceAsync(connection, tx);
            var issuedId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var meterId = Guid.NewGuid();
            await ExecuteAsync(connection, tx, $"""
                INSERT INTO "BuildingManagementInvoiceItems" ("Id", "InvoiceId")
                    VALUES ('{itemId:D}','{f.Draft:D}');
                INSERT INTO "BuildingManagementInvoiceItemMeterSources"
                    ("Id","InvoiceItemId")
                    VALUES ('{meterId:D}','{itemId:D}');
                """);

            var issue = InsertSql(f, issuedId, "FINAL-001");
            await ExecuteAsync(connection, tx, issue);
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementIssuedInvoiceLedger\""));

            // A different record ID is not enough to bypass unique draft,
            // evidence/review, invoice-number or room/contract period keys.
            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "FINAL-002"), "23505");
            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "FINAL-001"), "23505");

            // Source invoice must freeze even if an EF/caller tries to
            // change only ordinary audit metadata or delete the header.
            await ExpectFailureAsync(connection, tx, $"""
                UPDATE "BuildingManagementInvoices"
                SET "Status" = 1 WHERE "Id" = '{f.Draft:D}';
                """, "23514");
            await ExpectFailureAsync(connection, tx, $"""
                DELETE FROM "BuildingManagementInvoices"
                WHERE "Id" = '{f.Draft:D}';
                """, "23514");

            await ExpectFailureAsync(connection, tx, $"""
                UPDATE "BuildingManagementInvoiceItems"
                   SET "InvoiceId"='{f.Draft:D}'
                 WHERE "Id"='{itemId:D}';
                """, "23514");
            await ExpectFailureAsync(connection, tx, $"""
                DELETE FROM "BuildingManagementInvoiceItems"
                 WHERE "Id"='{itemId:D}';
                """, "23514");
            await ExpectFailureAsync(connection, tx, $"""
                INSERT INTO "BuildingManagementInvoiceItems"
                    ("Id","InvoiceId") VALUES ('{Guid.NewGuid():D}','{f.Draft:D}');
                """, "23514");
            await ExpectFailureAsync(connection, tx, $"""
                UPDATE "BuildingManagementInvoiceItemMeterSources"
                   SET "InvoiceItemId"='{itemId:D}'
                 WHERE "Id"='{meterId:D}';
                """, "23514");
            await ExpectFailureAsync(connection, tx, $"""
                INSERT INTO "BuildingManagementInvoiceItemMeterSources"
                    ("Id","InvoiceItemId") VALUES ('{Guid.NewGuid():D}','{itemId:D}');
                """, "23514");

            await ExpectFailureAsync(connection, tx,
                "UPDATE \"BuildingManagementIssuedInvoiceLedger\" " +
                "SET \"TotalAmount\" = 5", "23514");
            await ExpectFailureAsync(connection, tx,
                "DELETE FROM \"BuildingManagementIssuedInvoiceLedger\"", "23514");
            await ExpectFailureAsync(connection, tx,
                "TRUNCATE \"BuildingManagementIssuedInvoiceLedger\" CASCADE", "23514");
            await ExpectFailureAsync(connection, tx, down, "23514");
            Assert.Equal(1L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementIssuedInvoiceLedger\""));
        });
    }

    [Fact]
    public async Task SBLEDGER02_Invalid_Scope_Amounts_And_Timestamps_Are_Rejected()
    {
        await InScratchAsync(async (connection, tx, down) =>
        {
            var f = await SeedReviewedInvoiceAsync(connection, tx);

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "WRONG-CONTRACT")
                    .Replace($"'{f.Contract:D}'", $"'{Guid.NewGuid():D}'",
                        StringComparison.Ordinal), "23514");

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "BAD-BASE")
                    .Replace("3000000,0,3000000,", "1000,0,1000,",
                        StringComparison.Ordinal), "23514");

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "BAD-TOTAL")
                    .Replace("3000000,0,3000000,", "3000000,0,3000001,",
                        StringComparison.Ordinal), "23514");

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "EARLY-ISSUE")
                    .Replace("2026-10-29T01:00:00Z", "2026-10-29T00:00:00Z",
                        StringComparison.Ordinal), "23514");

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "BAD-DUE")
                    .Replace("2026-11-05", "2026-10-28",
                        StringComparison.Ordinal), "23514");

            await ExpectFailureAsync(connection, tx,
                InsertSql(f, Guid.NewGuid(), "MISSING-REVIEW")
                    .Replace($"'{f.Review:D}'", $"'{Guid.NewGuid():D}'",
                        StringComparison.Ordinal), "23514");

            // Stage a fresh draft/evidence/review that was NOT approved.
            var unreviewed = await SeedReviewedInvoiceAsync(
                connection, tx, markReviewed: false);
            await ExpectFailureAsync(connection, tx,
                InsertSql(unreviewed, Guid.NewGuid(), "NOT-REVIEWED"), "23514");

            Assert.Equal(0L, await CountAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementIssuedInvoiceLedger\""));
        });
    }

    [Fact]
    public async Task SBLEDGER03_Empty_Down_Preserves_Existing_Drafts_And_Reviews()
    {
        await InScratchAsync(async (connection, tx, down) =>
        {
            Assert.Equal(5L, await CountAsync(connection, tx,
                """
                SELECT count(*) FROM pg_trigger
                WHERE NOT tgisinternal AND
                  (tgrelid = '"BuildingManagementIssuedInvoiceLedger"'::regclass
                   OR (tgrelid = '"BuildingManagementInvoices"'::regclass
                       AND tgname = 'TR_BM_IssuedLedger_ProtectDraft')
                   OR (tgrelid = '"BuildingManagementInvoiceItems"'::regclass
                       AND tgname = 'TR_BM_IssuedLedger_ProtectItems')
                   OR (tgrelid = '"BuildingManagementInvoiceItemMeterSources"'::regclass
                       AND tgname = 'TR_BM_IssuedLedger_ProtectMeterSources'))
                """));
            await ExecuteAsync(connection, tx, down);
            Assert.Equal(1L, await CountAsync(connection, tx, """
                SELECT to_regclass('"BuildingManagementIssuedInvoiceLedger"') IS NULL
                """));
            Assert.Equal(1L, await CountAsync(connection, tx, """
                SELECT to_regclass('"BuildingManagementInvoices"') IS NOT NULL
                """));
            Assert.Equal(1L, await CountAsync(connection, tx, """
                SELECT to_regclass('"BuildingManagementInvoiceIssuanceReviews"') IS NOT NULL
                """));
        });
    }

    private static async Task<TestInvoice> SeedReviewedInvoiceAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, bool markReviewed = true)
    {
        var f = new TestInvoice(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), new string('a', 64));

        await ExecuteAsync(db, tx, $"""
            INSERT INTO "BuildingManagementInvoices"
                ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
            VALUES ('{f.Draft:D}','{f.Contract:D}','{f.Room:D}',2026,10,0);
            INSERT INTO "BuildingManagementMoveOutRequests" ("Id")
                VALUES ('{f.Request:D}');
            INSERT INTO "BuildingManagementFinalInvoiceIssueEvidence"
                ("Id","DraftInvoiceId","MoveOutRequestId","ContractId","RoomId",
                 "BillingYear","BillingMonth","PreparedByUserId",
                 "PreparedAtUtc","KnownBaseChargeTotal","EvidenceJson","EvidenceSha256")
            VALUES ('{f.Evidence:D}','{f.Draft:D}','{f.Request:D}',
                    '{f.Contract:D}','{f.Room:D}',2026,10,
                    '{Guid.NewGuid():D}','2026-10-29T00:00:00Z',
                    3000000,'[]','{f.SourceSha}');
            INSERT INTO "BuildingManagementInvoiceIssuanceReviews"
                ("Id","StagedEvidenceId","DraftInvoiceId","MoveOutRequestId",
                 "ContractId","RoomId","BuildingId","EvidenceSha256",
                 "SubmittedByUserId","SubmittedAtUtc","Status")
            VALUES ('{f.Review:D}','{f.Evidence:D}','{f.Draft:D}',
                    '{f.Request:D}','{f.Contract:D}','{f.Room:D}',
                    '{f.Building:D}','{f.SourceSha}','{f.Submitter:D}',
                    '2026-10-29T00:10:00Z',1);
            """);

        if (markReviewed)
            await ExecuteAsync(db, tx, $"""
                UPDATE "BuildingManagementInvoiceIssuanceReviews"
                SET "Status"=2, "ReviewedByUserId"='{f.Reviewer:D}',
                    "ReviewedAtUtc"='2026-10-29T00:30:00Z',
                    "ReviewReason"='Independently checked source scope'
                WHERE "Id"='{f.Review:D}';
                """);
        return f;
    }

    private static string InsertSql(TestInvoice f, Guid id, string number)
    {
        var terms = JsonSerializer.Serialize(new
        {
            DraftInvoiceId = f.Draft,
            EvidenceSha256 = f.SourceSha,
            BaseAmount = 3000000m,
            AuxiliaryAmount = 0m,
            TotalAmount = 3000000m,
            DueDate = "2026-11-05"
        });
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(terms))).ToLowerInvariant();
        return $"""
            INSERT INTO "BuildingManagementIssuedInvoiceLedger"
                ("Id","DraftInvoiceId","StagedEvidenceId","ReviewTicketId",
                 "MoveOutRequestId","ContractId","RoomId","BuildingId",
                 "BillingYear","BillingMonth","InvoiceNumber","IssuedByUserId",
                 "IssuedAtUtc","DueDate","BaseAmount","AuxiliaryAmount",
                 "TotalAmount","EvidenceSha256","FinancialTermsJson",
                 "FinancialTermsSha256")
            VALUES ('{id:D}','{f.Draft:D}','{f.Evidence:D}','{f.Review:D}',
                    '{f.Request:D}','{f.Contract:D}','{f.Room:D}',
                    '{f.Building:D}',2026,10,'{number}','{Guid.NewGuid():D}',
                    '2026-10-29T01:00:00Z','2026-11-05',3000000,0,3000000,
                    '{f.SourceSha}','{terms}','{hash}');
            """;
    }

    private static string MigrationSql(Migration migration, bool down)
    {
        var operations = down ? migration.DownOperations : migration.UpOperations;
        var script = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Single(operations);
        // The prerequisite evidence migration has different invariants.
        // Assert the ledger-specific guard only for the ledger migration.
        if (migration is SliceBIssuedInvoiceLedger)
            Assert.Contains(down ? "refuse rollback" : "TR_BM_IssuedLedger_ImmutableRow",
                script.Sql, StringComparison.Ordinal);
        return script.Sql;
    }

    private static async Task InScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> test)
    {
        var evidenceSql = MigrationSql(
            new SliceBFinalInvoiceEvidencePersistence(), down: false);
        var up = MigrationSql(new SliceBIssuedInvoiceLedger(), down: false);
        var down = MigrationSql(new SliceBIssuedInvoiceLedger(), down: true);
        var allowed = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var options = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false, CommandTimeout = 30
        };
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, connection.Database);
        var schema = "bm_issue_ledger_" + Guid.NewGuid().ToString("N");
        await using (var tx = await connection.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(connection, tx, $"CREATE SCHEMA \"{schema}\"");
                await ExecuteAsync(connection, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await ExecuteAsync(connection, tx, "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(connection, tx, "SET LOCAL statement_timeout = '20s'");
                await ExecuteAsync(connection, tx, """
                    CREATE TABLE "BuildingManagementInvoices" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "ContractId" uuid NOT NULL,
                        "RoomId" uuid NOT NULL,
                        "BillingYear" integer NOT NULL,
                        "BillingMonth" integer NOT NULL,
                        "Status" integer NOT NULL);
                    CREATE TABLE "BuildingManagementMoveOutRequests" (
                        "Id" uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE "BuildingManagementInvoiceItems" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "InvoiceId" uuid NOT NULL
                            REFERENCES "BuildingManagementInvoices" ("Id"));
                    CREATE TABLE "BuildingManagementInvoiceItemMeterSources" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "InvoiceItemId" uuid NOT NULL
                            REFERENCES "BuildingManagementInvoiceItems" ("Id"));
                    """);
                await ExecuteAsync(connection, tx, evidenceSql);
                await ExecuteAsync(connection, tx, up);
                await test(connection, tx, down);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }
        Assert.Equal(0L, await CountAsync(connection, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname = '{schema}'"));
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql, string expectedSqlState)
    {
        const string sp = "issued_ledger_probe";
        await ExecuteAsync(db, tx, $"SAVEPOINT {sp}");
        string? state = null;
        try { await ExecuteAsync(db, tx, sql); }
        catch (PostgresException ex) { state = ex.SqlState; }
        finally
        {
            await ExecuteAsync(db, tx, $"ROLLBACK TO SAVEPOINT {sp}");
            await ExecuteAsync(db, tx, $"RELEASE SAVEPOINT {sp}");
        }
        Assert.Equal(expectedSqlState, state);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
