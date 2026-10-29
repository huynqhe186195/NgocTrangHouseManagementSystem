using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Disposable PostgreSQL schemas only: never mutate a user's database.
/// This probes the SQL trigger itself, not application authorization.
/// </summary>
public sealed class SliceBBlockIssuedInvoiceAfterCloseoutPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBBlockIssuedInvoiceAfterCloseout().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBBlockIssuedInvoiceAfterCloseout().DownOperations
            .OfType<SqlOperation>()).Sql;

    [Fact]
    public async Task SBINVFREEZE01_Closeout_Blocks_Later_Issuance_But_Not_Other_Contracts()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var closed = Guid.NewGuid();
            var open = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{closed:D}'),('{open:D}');
                INSERT INTO "BuildingManagementIssuedInvoiceLedger" ("Id","ContractId")
                    VALUES ('{Guid.NewGuid():D}','{closed:D}');
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals" ("ContractId")
                    VALUES ('{closed:D}');
                """);

            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementIssuedInvoiceLedger" ("Id","ContractId")
                    VALUES ('{Guid.NewGuid():D}','{closed:D}')
                """);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementIssuedInvoiceLedger" ("Id","ContractId")
                    VALUES ('{Guid.NewGuid():D}','{open:D}')
                """);
            await Denied(db, tx, DownSql);
            Assert.Equal(1L, await Scalar(db, tx, $"""
                SELECT count(*) FROM "BuildingManagementIssuedInvoiceLedger"
                 WHERE "ContractId"='{closed:D}'
                """));
            Assert.Equal(1L, await Scalar(db, tx, $"""
                SELECT count(*) FROM "BuildingManagementIssuedInvoiceLedger"
                 WHERE "ContractId"='{open:D}'
                """));
        });
    }

    [Fact]
    public async Task SBINVFREEZE02_Empty_Closeout_Allows_Safe_Downgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname = 'TR_BM_IssuedLedger_RejectAfterDepositCloseout'
                """));
        });
    }

    [Fact]
    public async Task SBINVFREEZE03_Concurrent_Issuer_Waits_For_Approval_Then_Denies()
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var cs = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false, CommandTimeout = 25
        }.ConnectionString;
        var schema = "bm_invoice_freeze_" + Guid.NewGuid().ToString("N");
        var contract = Guid.NewGuid();
        await using var setup = new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, setup.Database);
        try
        {
            await Run(setup, null, $"""
                CREATE SCHEMA "{schema}";
                SET search_path TO "{schema}";
                """);
            await Run(setup, null, SchemaSql);
            await Run(setup, null, UpSql);
            await Run(setup, null,
                $"INSERT INTO \"BuildingManagementContracts\" VALUES ('{contract:D}')");

            await using var closer = new NpgsqlConnection(cs);
            await using var issuer = new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await issuer.OpenAsync();
            await Run(closer, null, $"SET search_path TO \"{schema}\"");
            await Run(issuer, null, $"SET search_path TO \"{schema}\"");
            await using var approvalTx = await closer.BeginTransactionAsync();
            await using var issuerTx = await issuer.BeginTransactionAsync();

            // Hold the same contract lock used by the actual approval trigger.
            await Run(closer, approvalTx,
                $"SELECT 1 FROM \"BuildingManagementContracts\" WHERE \"Id\"='{contract:D}' FOR UPDATE");
            var pending = Run(issuer, issuerTx, $"""
                INSERT INTO "BuildingManagementIssuedInvoiceLedger" ("Id","ContractId")
                    VALUES ('{Guid.NewGuid():D}','{contract:D}')
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted,
                "An invoice issuer may not bypass the in-flight contract closeout lock.");

            await Run(closer, approvalTx,
                $"INSERT INTO \"BuildingManagementContractDepositCloseoutApprovals\" VALUES ('{contract:D}')");
            await approvalTx.CommitAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23514", error.SqlState);
            await issuerTx.RollbackAsync();

            Assert.Equal(0L, await Scalar(setup, null,
                "SELECT count(*) FROM \"BuildingManagementIssuedInvoiceLedger\""));
        }
        finally
        {
            await Run(setup, null, "RESET search_path");
            await Run(setup, null, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L, await Scalar(setup, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementIssuedInvoiceLedger" (
            "Id" uuid PRIMARY KEY,
            "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts" ("Id"));
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "ContractId" uuid PRIMARY KEY REFERENCES "BuildingManagementContracts" ("Id"));
        """;

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> probe)
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(source)
            { Pooling = false, CommandTimeout = 25 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_invoice_freeze_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await Run(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await Run(db, tx, SchemaSql);
                await Run(db, tx, UpSql);
                await probe(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await Run(db, tx, "SAVEPOINT invoice_freeze_probe");
        string? code = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException exception) { code = exception.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT invoice_freeze_probe");
            await Run(db, tx, "RELEASE SAVEPOINT invoice_freeze_probe");
        }
        Assert.Equal("23514", code);
    }

    private static async Task Run(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
