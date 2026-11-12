using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Executes the ACTUAL migration SQL in a random, transaction-local
/// PostgreSQL schema. No public fallback, EF Migrate, credentials from an
/// application, or committed DDL. A rollback removes every test artifact.
/// </summary>
public sealed class SliceBFinalInvoiceEvidenceMigrationPostgreSql_Tests
{
    private const string MigrationId =
        "20261029200000_SliceBFinalInvoiceEvidencePersistence";

    [Fact]
    public async Task SBISSUEDB01_Evidence_Is_Immutable_And_Review_Is_Independent()
    {
        await InScratchAsync(async (db, tx, down) =>
        {
            var draft = Guid.NewGuid();
            var request = Guid.NewGuid();
            var evidence = Guid.NewGuid();
            var review = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            var building = Guid.NewGuid();
            var author = Guid.NewGuid();
            var submitter = Guid.NewGuid();
            var reviewer = Guid.NewGuid();
            var hash = new string('a', 64);
            await ExecuteAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoices" ("Id")
                    VALUES ('{draft:D}');
                INSERT INTO "BuildingManagementMoveOutRequests" ("Id")
                    VALUES ('{request:D}');
                """);

            string EvidenceSql(Guid id, Guid linkedDraft, string digest) => $"""
                INSERT INTO "BuildingManagementFinalInvoiceIssueEvidence"
                ("Id","DraftInvoiceId","MoveOutRequestId","ContractId","RoomId",
                 "BillingYear","BillingMonth","PreparedByUserId","PreparedAtUtc",
                 "KnownBaseChargeTotal","EvidenceJson","EvidenceSha256")
                VALUES ('{id:D}','{linkedDraft:D}','{request:D}','{contract:D}',
                        '{room:D}',2026,10,'{author:D}',
                        '2026-10-29T00:00:00Z',3025000,'[]','{digest}');
                """;

            string ReviewSql(Guid id, string time, Guid reviewerId, int status,
                string? reason = null, Guid? sourceId = null) => $"""
                INSERT INTO "BuildingManagementInvoiceIssuanceReviews"
                ("Id","StagedEvidenceId","DraftInvoiceId","MoveOutRequestId",
                 "ContractId","RoomId","BuildingId","EvidenceSha256",
                 "SubmittedByUserId","SubmittedAtUtc","Status",
                 "ReviewedByUserId","ReviewedAtUtc","ReviewReason")
                VALUES ('{id:D}','{(sourceId ?? evidence):D}','{draft:D}',
                        '{request:D}','{contract:D}','{room:D}',
                        '{building:D}','{hash}','{submitter:D}','{time}',
                        {status},
                        {(reviewerId == Guid.Empty ? "NULL" : $"'{reviewerId:D}'")},
                        {(reviewerId == Guid.Empty ? "NULL" : "'2026-10-29T00:03:00Z'")},
                        {(reason is null ? "NULL" : $"'{reason}'")});
                """;

            await ExecuteAsync(db, tx, EvidenceSql(evidence, draft, hash));
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementFinalInvoiceIssueEvidence\""));

            // A second row with the same invoice or same contract/period
            // would permit double-billing and must fail at SQL level.
            await ExpectFailureAsync(db, tx,
                EvidenceSql(Guid.NewGuid(), draft, hash), "23505");
            // Use another billing period so this case isolates the
            // missing Draft FK rather than the period-uniqueness checks.
            await ExpectFailureAsync(db, tx,
                EvidenceSql(Guid.NewGuid(), Guid.NewGuid(), hash)
                    .Replace(",2026,10,", ",2026,11,",
                        StringComparison.Ordinal), "23503");
            await ExpectFailureAsync(db, tx,
                "UPDATE \"BuildingManagementFinalInvoiceIssueEvidence\" " +
                "SET \"KnownBaseChargeTotal\" = 1", "23514");
            await ExpectFailureAsync(db, tx,
                "DELETE FROM \"BuildingManagementFinalInvoiceIssueEvidence\"", "23514");
            await ExpectFailureAsync(db, tx,
                "TRUNCATE \"BuildingManagementFinalInvoiceIssueEvidence\" CASCADE", "23514");

            await ExpectFailureAsync(db, tx,
                ReviewSql(Guid.NewGuid(), "2026-10-28T23:59:00Z",
                    Guid.Empty, 1), "23514");
            await ExpectFailureAsync(db, tx,
                ReviewSql(Guid.NewGuid(), "2026-10-29T00:01:00Z",
                    submitter, 2, "Self review is invalid"), "23514");
            await ExpectFailureAsync(db, tx,
                ReviewSql(Guid.NewGuid(), "2026-10-29T00:01:00Z",
                    Guid.Empty, 1, sourceId: Guid.NewGuid()), "23514");

            await ExecuteAsync(db, tx,
                ReviewSql(review, "2026-10-29T00:01:00Z", Guid.Empty, 1));
            await ExpectFailureAsync(db, tx,
                ReviewSql(Guid.NewGuid(), "2026-10-29T00:01:00Z",
                    Guid.Empty, 1), "23505");

            await ExpectFailureAsync(db, tx, $"""
                UPDATE "BuildingManagementInvoiceIssuanceReviews"
                SET "Status" = 2, "ReviewedByUserId" = '{submitter:D}',
                    "ReviewedAtUtc" = '2026-10-29T00:02:00Z',
                    "ReviewReason" = 'Unauthorized self review'
                WHERE "Id" = '{review:D}';
                """, "23514");

            await ExecuteAsync(db, tx, $"""
                UPDATE "BuildingManagementInvoiceIssuanceReviews"
                SET "Status" = 2, "ReviewedByUserId" = '{reviewer:D}',
                    "ReviewedAtUtc" = '2026-10-29T00:03:00Z',
                    "ReviewReason" = 'Independent evidence review only'
                WHERE "Id" = '{review:D}';
                """);
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoiceIssuanceReviews\" " +
                "WHERE \"Status\" = 2"));

            await ExpectFailureAsync(db, tx,
                "UPDATE \"BuildingManagementInvoiceIssuanceReviews\" " +
                "SET \"Status\" = 3", "23514");
            await ExpectFailureAsync(db, tx,
                "DELETE FROM \"BuildingManagementInvoiceIssuanceReviews\"", "23514");
            await ExpectFailureAsync(db, tx,
                "TRUNCATE \"BuildingManagementInvoiceIssuanceReviews\"", "23514");
            await ExpectFailureAsync(db, tx, down, "23514");

            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoiceIssuanceReviews\""));
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementFinalInvoiceIssueEvidence\""));
        });
    }

    [Fact]
    public async Task SBISSUEDB02_Empty_Staged_Tables_Can_Roll_Back_Safely()
    {
        await InScratchAsync(async (db, tx, down) =>
        {
            Assert.Equal(4L, await CountAsync(db, tx,
                """
                SELECT count(*) FROM pg_trigger
                WHERE NOT tgisinternal AND
                    tgrelid IN (
                    '"BuildingManagementFinalInvoiceIssueEvidence"'::regclass,
                    '"BuildingManagementInvoiceIssuanceReviews"'::regclass)
                """));
            await ExecuteAsync(db, tx, down);
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT to_regclass('\"BuildingManagementFinalInvoiceIssueEvidence\"') IS NULL"));
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT to_regclass('\"BuildingManagementInvoiceIssuanceReviews\"') IS NULL"));
            Assert.Equal(1L, await CountAsync(db, tx,
                "SELECT to_regclass('\"BuildingManagementInvoices\"') IS NOT NULL"));
            Assert.Equal(0L, await CountAsync(db, tx,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n " +
                "ON p.pronamespace = n.oid WHERE n.nspname = current_schema() " +
                "AND p.proname LIKE 'BM_GuardFinalInvoiceReview'"));
        });
    }

    private static string OperationSql(bool down)
    {
        var migration = new SliceBFinalInvoiceEvidencePersistence();
        var operations = down ? migration.DownOperations : migration.UpOperations;
        var scripts = operations.OfType<SqlOperation>().ToArray();
        Assert.Single(scripts);
        Assert.Single(operations);
        Assert.Contains(down ? "refuse rollback" : "CK_BM_FinalIssueEvidence_Hash",
            scripts[0].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP SCHEMA", scripts[0].Sql,
            StringComparison.OrdinalIgnoreCase);
        return scripts[0].Sql;
    }

    private static async Task InScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> action)
    {
        var up = OperationSql(down: false);
        var down = OperationSql(down: true);
        var approved = BuildingManagementPostgreSqlTestModule
            .RequireSafeConnectionString();
        var options = new NpgsqlConnectionStringBuilder(approved)
        {
            Pooling = false,
            CommandTimeout = 30
        };
        await using var db = new NpgsqlConnection(options.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            db.Database);
        var schema = "bm_final_issue_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await ExecuteAsync(db, tx,
                    $"SET LOCAL search_path TO \"{schema}\"");
                await ExecuteAsync(db, tx, "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(db, tx, "SET LOCAL statement_timeout = '20s'");
                // No public in search_path: an absent dependency fails
                // instead of touching persistent integration objects.
                await ExecuteAsync(db, tx, """
                    CREATE TABLE "BuildingManagementInvoices" (
                        "Id" uuid PRIMARY KEY);
                    CREATE TABLE "BuildingManagementMoveOutRequests" (
                        "Id" uuid PRIMARY KEY);
                    """);
                await ExecuteAsync(db, tx, up);
                await action(db, tx, down);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }
        Assert.Equal(0L, await CountAsync(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname = '{schema}'"));
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection db, NpgsqlTransaction tx,
        string sql, string expectedState)
    {
        const string savepoint = "slice_b_probe";
        await ExecuteAsync(db, tx, $"SAVEPOINT {savepoint}");
        string? state = null;
        try
        {
            await ExecuteAsync(db, tx, sql);
        }
        catch (PostgresException error)
        {
            state = error.SqlState;
        }
        finally
        {
            await ExecuteAsync(db, tx, $"ROLLBACK TO SAVEPOINT {savepoint}");
            await ExecuteAsync(db, tx, $"RELEASE SAVEPOINT {savepoint}");
        }
        Assert.Equal(expectedState, state);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
