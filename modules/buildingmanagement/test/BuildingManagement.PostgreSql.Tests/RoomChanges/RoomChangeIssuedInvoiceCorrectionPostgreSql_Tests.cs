using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// P2.1c — the real database-level defenders around an issued-invoice rent
/// correction, exercised as two independent sessions rather than as one
/// sequential fault-injection run.
///
/// These tests mount the ACTUAL committed migration
/// (20261029223000_Phase2G3AuditedMonthlyAdjustments) — the same DDL the
/// application ships — into a throwaway schema, so the append-only trigger,
/// the independent-reviewer trigger, the no-truncate trigger and the rollback
/// guard are the production definitions, not a paraphrase of them.
/// </summary>
public sealed class RoomChangeIssuedInvoiceCorrectionPostgreSql_Tests
{
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string Up = string.Join("\n",
        new Phase2G3AuditedMonthlyAdjustments().UpOperations
            .OfType<SqlOperation>().Select(x => x.Sql));

    private static readonly string Down = string.Join("\n",
        new Phase2G3AuditedMonthlyAdjustments().DownOperations
            .OfType<SqlOperation>().Select(x => x.Sql));

    // The migration FK-references the issued ledger and its Down guard names
    // the allocation table, so both must exist first. Only the columns the
    // triggers actually read are needed.
    private const string Stub = """
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid, "BuildingId" uuid,
          "CandidateSha256" varchar(64),
          "TotalAmount" numeric(18,0),
          "IssuedAtUtc" timestamptz
        );
        CREATE TABLE "BuildingManagementMonthlyInvoicePaymentAllocation"(
          "Id" uuid PRIMARY KEY,
          "IssuedInvoiceId" uuid, "ContractId" uuid,
          "PaymentId" uuid, "Amount" numeric(18,0),
          "PaidAtUtc" timestamptz, "RecordedAtUtc" timestamptz,
          "PaymentNumberSnapshot" varchar(64),
          "BankTransactionReferenceSnapshot" varchar(128)
        );
        """;

    private static readonly DateTime IssuedAt =
        new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        Guid Invoice, Guid Contract, Guid Building, Guid Proposer, Guid Reviewer);

    [Fact]
    public async Task RICCPG01_Concurrent_Corrections_On_One_Invoice_Serialize_ToOne()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_concurrent_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            var f = NewFixture();
            await Execute(admin, null, Seed(f));

            // Two independent users both believe the invoice is uncorrected
            // and both record the SAME economic correction. The planner's
            // deterministic id cannot stop this on its own: two clients, two
            // ids, one invoice. The database must be the last line.
            var outcomes = await Task.WhenAll(
                    ProposeAsync(safe, schema, f, -1_645_161m),
                    ProposeAsync(safe, schema, f, -1_645_161m))
                .WaitAsync(TimeSpan.FromSeconds(45));

            Assert.Equal(1, outcomes.Count(x => x is null));
            var loser = Assert.Single(outcomes, x => x is not null)!;
            Assert.Contains("23505", ErrorCodes(loser));

            Assert.Equal(1L, await Count(admin));
            Assert.Equal(0L, await CountDecisions(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task RICCPG02_Replaying_The_Same_Correction_Does_Not_Double_Charge()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_replay_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            var f = NewFixture();
            await Execute(admin, null, Seed(f));

            var correction = Propose(f, f.Proposer, -1_645_161m);
            await Execute(admin, null, correction);

            // Re-submitting after a client restart must not post a second
            // financial statement against the same invoice and evidence.
            var replay = await Assert.ThrowsAsync<PostgresException>(
                () => Execute(admin, null, correction));
            Assert.Equal("23505", replay.SqlState);
            Assert.Equal(1L, await Count(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task RICCPG03_Sql_Bypass_Attempts_Are_Refused_By_The_Database()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_bypass_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            var f = NewFixture();
            await Execute(admin, null, Seed(f));
            await Execute(admin, null, Propose(f, f.Proposer, -1_645_161m));

            // Direct SQL cannot rewrite or delete the historical correction,
            // cannot truncate the audit table, and cannot smuggle in a
            // proposal anchored to a different issued snapshot.
            await Refuses(admin, $"""
                UPDATE "BuildingManagementMonthlyInvoiceAdjustments"
                   SET "SignedDeltaAmount" = -1
                 WHERE "IssuedInvoiceId" = '{f.Invoice:D}'
                """, "23514");
            await Refuses(admin, """
                DELETE FROM "BuildingManagementMonthlyInvoiceAdjustments"
                """, "23514");
            await Refuses(admin, """
                TRUNCATE "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
                """, "23514");
            // The parent table is FK-referenced by the decision table, so
            // PostgreSQL also refuses a plain TRUNCATE; the guard must still
            // hold when CASCADE reaches both tables in one statement.
            await Refuses(admin, """
                TRUNCATE "BuildingManagementMonthlyInvoiceAdjustments" CASCADE
                """, "23514");
            await Refuses(admin, Propose(f, f.Proposer, -50_000m,
                new string('b', 64), "BYPASS-ATTEMPT-EVIDENCE-001"), "23514");

            Assert.Equal(1L, await Count(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task RICCPG04_Independent_Review_Cannot_Be_The_Proposer()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_reviewer_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            var f = NewFixture();
            await Execute(admin, null, Seed(f));
            var adjustment = Guid.NewGuid();
            await Execute(admin, null, Propose(f, f.Proposer, -1_645_161m, id: adjustment));

            await Refuses(admin, Decision(adjustment, f, f.Proposer), "23514");
            await Execute(admin, null, Decision(adjustment, f, f.Reviewer));
            Assert.Equal(1L, await CountDecisions(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task RICCPG05_Credit_Below_Captured_Receipts_Is_Refused()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_receipts_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            var f = NewFixture();
            await Execute(admin, null, Seed(f));
            var adjustment = Guid.NewGuid();
            // 3,000,000 issued, the D01 correction would leave 1,354,839,
            // but the tenant has already paid 2,000,000 of it.
            await Execute(admin, null, Propose(f, f.Proposer, -1_645_161m, id: adjustment));
            await Execute(admin, null, Receipt(f, 2_000_000m));

            await Refuses(admin, Decision(adjustment, f, f.Reviewer), "23514");
            Assert.Equal(0L, await CountDecisions(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task RICCPG06_Migration_Rollback_Is_Refused_While_History_Exists()
    {
        var safe = SafeConnection();
        var schema = "bm_p21_rollback_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(safe);
        await admin.OpenAsync();
        try
        {
            await Execute(admin, null, $"""CREATE SCHEMA "{schema}" """);
            await Execute(admin, null, $"""SET search_path TO "{schema}" """);
            await Execute(admin, null, Stub);
            await Execute(admin, null, Up);

            // With no corrections on file, the migration's own Down is a
            // clean and complete reversal — nothing is left behind.
            // Down() opens with LOCK TABLE, so it must run inside a
            // transaction block, exactly as EF Core would run it.
            await using (var tx = await admin.BeginTransactionAsync())
            {
                await Execute(admin, tx, Down);
                await tx.CommitAsync();
            }

            Assert.Equal(0L, await CountAdjustmentTables(admin));

            // Remount and post a real correction; rollback must now refuse
            // rather than silently destroy an audited financial record.
            await Execute(admin, null, Up);
            var f = NewFixture();
            await Execute(admin, null, Seed(f));
            await Execute(admin, null, Propose(f, f.Proposer, -1_645_161m));

            await using (var tx = await admin.BeginTransactionAsync())
            {
                var refusal = await Assert.ThrowsAsync<PostgresException>(
                    () => Execute(admin, tx, Down));
                Assert.Equal("23514", refusal.SqlState);
                await tx.RollbackAsync();
            }

            Assert.Equal(1L, await Count(admin));
        }
        finally
        {
            await Execute(admin, null, $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    // ---- the two independent sessions of the concurrency scenario ----

    private static async Task<Exception?> ProposeAsync(
        string connectionString, string schema, Fixture f, decimal delta)
    {
        try
        {
            await using var connection = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(connectionString)
                    { Pooling = false, CommandTimeout = 45 }.ConnectionString);
            await connection.OpenAsync();
            await Execute(connection, null, $"""SET search_path TO "{schema}" """);
            await Execute(connection, null, Propose(f, f.Proposer, delta));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static IEnumerable<string> ErrorCodes(Exception error)
    {
        for (Exception? ex = error; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException pg) yield return pg.SqlState;
    }

    // ---- fixtures ----

    private static Fixture NewFixture() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static string Seed(Fixture f) => $"""
        INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
         ('{f.Invoice:D}','{f.Contract:D}','{f.Building:D}','{Hash}',
          3000000,'{IssuedAt:yyyy-MM-ddTHH:mm:ssZ}');
        """;

    private static string Propose(Fixture f, Guid proposer, decimal delta,
        string hash = Hash,
        string evidence = "H4-ROOT-SIGNED-OCTOBER-SOURCE-RENT-CORRECTION",
        Guid? id = null) => $"""
        INSERT INTO "BuildingManagementMonthlyInvoiceAdjustments" VALUES
         ('{(id ?? Guid.NewGuid()):D}','{f.Invoice:D}','{f.Contract:D}',
          '{f.Building:D}','{hash}',{delta},
          'D01 target-day source rent credit on the issued invoice',
          '{evidence}',
          '{proposer:D}','2026-10-20T10:00:00Z');
        """;

    private static string Decision(Guid adjustment, Fixture f, Guid reviewer) => $"""
        INSERT INTO "BuildingManagementMonthlyInvoiceAdjustmentDecisions" VALUES
         ('{Guid.NewGuid():D}','{adjustment:D}','{f.Invoice:D}','{f.Contract:D}',
          -1645161,'{reviewer:D}','2026-10-21T10:00:00Z',true,
          'Independent rent proration review accepted');
        """;

    private static string Receipt(Fixture f, decimal amount) => $"""
        INSERT INTO "BuildingManagementMonthlyInvoicePaymentAllocation" VALUES
         ('{Guid.NewGuid():D}','{f.Invoice:D}','{f.Contract:D}','{Guid.NewGuid():D}',
          {amount},'2026-10-18T09:00:00Z','2026-10-18T10:00:00Z',
          'P21-BANK-RECEIPT-001','P21-BANK-RECEIPT-001');
        """;

    private static string SafeConnection()
        => new NpgsqlConnectionStringBuilder(
                BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            { Pooling = false, CommandTimeout = 45 }.ConnectionString;

    private static async Task<long> Scalar(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static Task<long> Count(NpgsqlConnection db) => Scalar(db,
        """SELECT count(*) FROM "BuildingManagementMonthlyInvoiceAdjustments" """);

    private static Task<long> CountDecisions(NpgsqlConnection db) => Scalar(db,
        """SELECT count(*) FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" """);

    private static Task<long> CountAdjustmentTables(NpgsqlConnection db) => Scalar(db,
        """
        SELECT count(*) FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = current_schema()
           AND c.relname IN ('BuildingManagementMonthlyInvoiceAdjustments',
                             'BuildingManagementMonthlyInvoiceAdjustmentDecisions')
        """);

    private static async Task Refuses(
        NpgsqlConnection db, string sql, string expectedSqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => Execute(db, null, sql));
        Assert.Equal(expectedSqlState, error.SqlState);
    }

    private static async Task Execute(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
