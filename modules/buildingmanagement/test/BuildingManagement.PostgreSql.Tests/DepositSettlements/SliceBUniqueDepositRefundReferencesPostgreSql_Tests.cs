using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Each test uses a random isolated schema in the disposable PostgreSQL
/// test database, never an application DB or an external bank account.
/// </summary>
public sealed class SliceBUniqueDepositRefundReferencesPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBUniqueDepositRefundReferences().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBUniqueDepositRefundReferences().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL UNIQUE,
          "RefundTransactionReference" varchar(128) NULL
        );
        """;

    [Fact]
    public async Task SBREF01_Refund_Reference_Is_Unique_Case_And_Whitespace_Insensitive()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","RefundTransactionReference")
                VALUES ('{Guid.NewGuid():D}','{Guid.NewGuid():D}','BANK-20261029-REF-001');
                """);
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","RefundTransactionReference")
                VALUES ('{Guid.NewGuid():D}','{Guid.NewGuid():D}',' bank-20261029-ref-001 ');
                """, "23505");
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","RefundTransactionReference")
                VALUES ('{Guid.NewGuid():D}','{Guid.NewGuid():D}','BANK-20261029-REF-002'),
                       ('{Guid.NewGuid():D}','{Guid.NewGuid():D}',NULL),
                       ('{Guid.NewGuid():D}','{Guid.NewGuid():D}',NULL);
                """);
            await Denied(db, tx, DownSql, "23514");
            Assert.Equal(4L, await Scalar(db, tx,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));
        });
    }

    [Fact]
    public async Task SBREF02_Empty_History_Allows_Downgrade_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'UX_BM_DepositCloseout_RefundBankReference'
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'UX_BM_DepositCloseout_RefundBankReference'
                """));
        });
    }

    [Fact]
    public async Task SBREF03_Concurrent_Same_Refund_Reference_Commits_Only_Once()
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var cs = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false, CommandTimeout = 25
        }.ConnectionString;
        var schema = "bm_refund_ref_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, setup.Database);
        try
        {
            await Run(setup, null, $"CREATE SCHEMA \"{schema}\"");
            await Run(setup, null, $"SET search_path TO \"{schema}\"");
            await Run(setup, null, SchemaSql);
            await Run(setup, null, UpSql);

            await using var first = new NpgsqlConnection(cs);
            await using var second = new NpgsqlConnection(cs);
            await first.OpenAsync();
            await second.OpenAsync();
            await Run(first, null, $"SET search_path TO \"{schema}\"");
            await Run(second, null, $"SET search_path TO \"{schema}\"");
            await using var firstTx = await first.BeginTransactionAsync();
            await using var secondTx = await second.BeginTransactionAsync();

            await Run(first, firstTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","RefundTransactionReference")
                VALUES ('{Guid.NewGuid():D}','{Guid.NewGuid():D}','BANK-20261029-REF-CONCURRENT');
                """);
            var pending = Run(second, secondTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","RefundTransactionReference")
                VALUES ('{Guid.NewGuid():D}','{Guid.NewGuid():D}',' bank-20261029-ref-concurrent ');
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted,
                "A duplicate refund reference must await the first transaction.");
            await firstTx.CommitAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23505", error.SqlState);
            await secondTx.RollbackAsync();
            Assert.Equal(1L, await Scalar(setup, null,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));
        }
        finally
        {
            await Run(setup, null, "RESET search_path");
            await Run(setup, null, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L, await Scalar(setup, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> probe)
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(source)
            { Pooling = false, CommandTimeout = 25 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_refund_ref_" + Guid.NewGuid().ToString("N");
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
        NpgsqlConnection db, NpgsqlTransaction tx, string sql, string expected)
    {
        await Run(db, tx, "SAVEPOINT refund_reference_probe");
        string? code = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException error) { code = error.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT refund_reference_probe");
            await Run(db, tx, "RELEASE SAVEPOINT refund_reference_probe");
        }
        Assert.Equal(expected, code);
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
