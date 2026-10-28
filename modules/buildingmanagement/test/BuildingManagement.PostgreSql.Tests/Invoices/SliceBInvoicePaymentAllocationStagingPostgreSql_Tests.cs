using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Actual Up/Down SQL in a rolled-back random local PostgreSQL schema.
/// Test scope has no public schema fallback or migration into app databases.
/// </summary>
public sealed class SliceBInvoicePaymentAllocationStagingPostgreSql_Tests
{
    [Fact]
    public async Task SBALLOCDB01_Mismatched_Duplicate_And_Rewritten_Receipts_Are_Denied()
    {
        await InScratchAsync(async (connection, tx, down) =>
        {
            var contract = Guid.NewGuid();
            var invoice = Guid.NewGuid();
            var payment = Guid.NewGuid();
            var journal = Guid.NewGuid();
            var actor = Guid.NewGuid();

            string PaymentSql(Guid id, int purpose, int status, bool deleted = false)
                => $"""
                   INSERT INTO "BuildingManagementPayments"
                   ("Id","Purpose","Status","ContractId","InvoiceId",
                    "Amount","PaymentNumber","PaidAt","TransactionReference","IsDeleted")
                   VALUES ('{id:D}',{purpose},{status},'{contract:D}','{invoice:D}',
                           150000,'PAY-{id:N}','2026-10-29T01:00:00Z',
                           'TRACE-{id:N}',{(deleted ? "TRUE" : "FALSE")});
                   """;

            string JournalSql(Guid id, Guid paymentId, Guid issuedInvoice,
                Guid assignedContract, decimal amount = 150000m)
                => $"""
                   INSERT INTO "BuildingManagementInvoicePaymentAllocationJournal"
                   ("Id","PaymentId","IssuedInvoiceId","ContractId","Amount",
                    "PaymentNumberSnapshot","PaymentPaidAtUtc",
                    "PaymentTransactionReferenceSnapshot",
                    "RecordedByUserId","RecordedAtUtc")
                   VALUES ('{id:D}','{paymentId:D}','{issuedInvoice:D}',
                           '{assignedContract:D}',{amount},
                           'PAY-{paymentId:N}','2026-10-29T01:00:00Z',
                           'TRACE-{paymentId:N}','{actor:D}','2026-10-29T01:05:00Z');
                   """;

            var pending = Guid.NewGuid();
            var deposit = Guid.NewGuid();
            var deleted = Guid.NewGuid();
            await ExecuteAsync(connection, tx, PaymentSql(payment, 3, 2));
            await ExecuteAsync(connection, tx, PaymentSql(pending, 3, 1));
            await ExecuteAsync(connection, tx, PaymentSql(deposit, 2, 2));
            await ExecuteAsync(connection, tx, PaymentSql(deleted, 3, 2, true));

            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), pending, invoice, contract), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), deposit, invoice, contract), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), deleted, invoice, contract), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), payment, Guid.NewGuid(), contract), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), payment, invoice, Guid.NewGuid()), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), payment, invoice, contract, 150001m), "23514");
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), Guid.NewGuid(), invoice, contract), "23503");

            await ExecuteAsync(connection, tx,
                JournalSql(journal, payment, invoice, contract));
            Assert.Equal(1L, await ScalarAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoicePaymentAllocationJournal\""));
            await ExpectFailureAsync(connection, tx,
                JournalSql(Guid.NewGuid(), payment, invoice, contract), "23505");

            await ExpectFailureAsync(connection, tx,
                "UPDATE \"BuildingManagementInvoicePaymentAllocationJournal\" " +
                "SET \"Amount\" = 1", "23514");
            await ExpectFailureAsync(connection, tx,
                "DELETE FROM \"BuildingManagementInvoicePaymentAllocationJournal\"", "23514");
            await ExpectFailureAsync(connection, tx,
                "TRUNCATE \"BuildingManagementInvoicePaymentAllocationJournal\"", "23514");
            await ExpectFailureAsync(connection, tx, down, "23514");

            Assert.Equal(1L, await ScalarAsync(connection, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoicePaymentAllocationJournal\""));
        });
    }

    [Fact]
    public async Task SBALLOCDB02_Empty_Downgrade_Preserves_Payment_Source()
    {
        await InScratchAsync(async (connection, tx, down) =>
        {
            Assert.Equal(2L, await ScalarAsync(connection, tx, """
                SELECT count(*) FROM pg_trigger
                 WHERE NOT tgisinternal
                   AND tgrelid = '"BuildingManagementInvoicePaymentAllocationJournal"'::regclass
                """));
            await ExecuteAsync(connection, tx, down);
            Assert.Equal(1L, await ScalarAsync(connection, tx, """
                SELECT to_regclass('"BuildingManagementInvoicePaymentAllocationJournal"') IS NULL
                """));
            Assert.Equal(1L, await ScalarAsync(connection, tx, """
                SELECT to_regclass('"BuildingManagementPayments"') IS NOT NULL
                """));
        });
    }

    private static string ActualMigrationSql(bool downgrade)
    {
        var migration = new SliceBInvoicePaymentAllocationStaging();
        var operations = downgrade ? migration.DownOperations : migration.UpOperations;
        var operation = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Single(operations);
        Assert.Contains(downgrade ? "refuse rollback" : "UX_BM_InvoiceAllocation_Payment",
            operation.Sql, StringComparison.Ordinal);
        return operation.Sql;
    }

    private static async Task InScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> test)
    {
        var up = ActualMigrationSql(downgrade: false);
        var down = ActualMigrationSql(downgrade: true);
        var connectionString =
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var opts = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, CommandTimeout = 30
        };
        await using var connection = new NpgsqlConnection(opts.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            connection.Database);
        var schema = "bm_alloc_stage_" + Guid.NewGuid().ToString("N");
        await using (var tx = await connection.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(connection, tx, $"CREATE SCHEMA \"{schema}\"");
                await ExecuteAsync(connection, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await ExecuteAsync(connection, tx, "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(connection, tx, "SET LOCAL statement_timeout = '20s'");
                await ExecuteAsync(connection, tx, """
                    CREATE TABLE "BuildingManagementPayments" (
                        "Id" uuid PRIMARY KEY,
                        "Purpose" integer NOT NULL,
                        "Status" integer NOT NULL,
                        "ContractId" uuid NULL,
                        "InvoiceId" uuid NULL,
                        "Amount" numeric(18,0) NOT NULL,
                        "PaymentNumber" varchar(64) NOT NULL,
                        "PaidAt" timestamptz NULL,
                        "TransactionReference" varchar(128) NULL,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE);
                    """);
                await ExecuteAsync(connection, tx, up);
                await test(connection, tx, down);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }
        Assert.Equal(0L, await ScalarAsync(connection, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname = '{schema}'"));
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, string sqlState)
    {
        const string sp = "bm_allocation_probe";
        await ExecuteAsync(connection, tx, $"SAVEPOINT {sp}");
        string? actual = null;
        try { await ExecuteAsync(connection, tx, sql); }
        catch (PostgresException error) { actual = error.SqlState; }
        finally
        {
            await ExecuteAsync(connection, tx, $"ROLLBACK TO SAVEPOINT {sp}");
            await ExecuteAsync(connection, tx, $"RELEASE SAVEPOINT {sp}");
        }
        Assert.Equal(sqlState, actual);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
