using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Runs the actual original trigger followed by the corrective migration
/// inside a disposable random schema. Never touches application databases.
/// </summary>
public sealed class SliceBLockBothDepositSettlementContractsPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBLockBothDepositSettlementContracts().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBLockBothDepositSettlementContracts().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid PRIMARY KEY
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "ContractId" uuid NOT NULL,
            "RefundSettlementId" uuid NULL,
            "ForfeitSettlementId" uuid NULL
        );
        CREATE TABLE "BuildingManagementDepositSettlements" (
            "Id" uuid PRIMARY KEY,
            "SourceContractId" uuid NULL,
            "TargetContractId" uuid NULL
        );
        """;

    [Fact]
    public async Task SBLOCK01_Completed_Contracts_Reject_Each_Old_New_Transfer_Endpoint()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var source = Guid.NewGuid();
            var target = Guid.NewGuid();
            var unrelated = Guid.NewGuid();
            var row = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts"
                VALUES ('{source:D}'),('{target:D}'),('{unrelated:D}');
                INSERT INTO "BuildingManagementDepositSettlements"
                VALUES ('{row:D}','{source:D}','{unrelated:D}');
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                    ("ContractId") VALUES ('{target:D}');
                """);
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementDepositSettlements"
                VALUES ('{Guid.NewGuid():D}','{source:D}','{target:D}');
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                SET "TargetContractId" = '{target:D}'
                WHERE "Id" = '{row:D}';
                """);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementDepositSettlements"
                VALUES ('{Guid.NewGuid():D}','{source:D}','{unrelated:D}');
                """);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                    ("ContractId") VALUES ('{source:D}');
                """);
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementDepositSettlements"
                WHERE "Id" = '{row:D}';
                """);
            await Denied(db, tx, DownSql);
            Assert.Equal(2L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositCloseoutApprovals" """));
        });
    }

    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task SBLOCK02_Settlement_Mutation_Waits_For_Receiving_Contract_Approval(
        string mutation)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        {
            Pooling = false, CommandTimeout = 25
        }.ConnectionString;
        var schema = "bm_settlement_lock_" + Guid.NewGuid().ToString("N");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var settlement = Guid.NewGuid();

        await using var setup = new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, setup.Database);
        try
        {
            await Run(setup, null, $"CREATE SCHEMA \"{schema}\"");
            await Run(setup, null, $"SET search_path TO \"{schema}\"");
            await Run(setup, null, SchemaSql);
            await Run(setup, null, OriginalGuardSql());
            await Run(setup, null, UpSql);
            await Run(setup, null, $"""
                INSERT INTO "BuildingManagementContracts"
                VALUES ('{source:D}'),('{target:D}'),('{other:D}');
                """);
            if (mutation != "INSERT")
            {
                await Run(setup, null, $"""
                    INSERT INTO "BuildingManagementDepositSettlements"
                    VALUES ('{settlement:D}','{source:D}',
                            '{(mutation == "DELETE" ? target : other):D}');
                    """);
            }

            await using var closer = new NpgsqlConnection(cs);
            await using var editor = new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await editor.OpenAsync();
            await Run(closer, null, $"SET search_path TO \"{schema}\"");
            await Run(editor, null, $"SET search_path TO \"{schema}\"");

            await using var closeTx = await closer.BeginTransactionAsync();
            await using var editTx = await editor.BeginTransactionAsync();

            await Run(closer, closeTx, $"""
                SELECT "Id" FROM "BuildingManagementContracts"
                WHERE "Id" = '{target:D}' FOR UPDATE;
                """);
            var action = mutation switch
            {
                "INSERT" => $"""
                    INSERT INTO "BuildingManagementDepositSettlements"
                    VALUES ('{settlement:D}','{source:D}','{target:D}');
                    """,
                "UPDATE" => $"""
                    UPDATE "BuildingManagementDepositSettlements"
                    SET "TargetContractId"='{target:D}' WHERE "Id"='{settlement:D}';
                    """,
                "DELETE" => $"""
                    DELETE FROM "BuildingManagementDepositSettlements"
                    WHERE "Id"='{settlement:D}';
                    """,
                _ => throw new InvalidOperationException(mutation)
            };
            var pending = Run(editor, editTx, action);
            await Task.Delay(180);
            Assert.False(pending.IsCompleted);

            await Run(closer, closeTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                    ("ContractId") VALUES ('{target:D}');
                """);
            await closeTx.CommitAsync();

            var refused = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23514", refused.SqlState);
            await editTx.RollbackAsync();
            Assert.Equal(mutation == "INSERT" ? 0L : 1L,
                await Scalar(setup, null,
                    """SELECT count(*) FROM "BuildingManagementDepositSettlements" """));
        }
        finally
        {
            await Run(setup, null, "RESET search_path");
            await Run(setup, null, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L, await Scalar(setup, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    [Fact]
    public async Task SBLOCK03_Empty_Downgrade_Restores_Original_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT (position('ORDER BY c."Id" FOR UPDATE'
                    IN pg_get_functiondef('"BM_GuardFinalizedDepositSettlements"()'::regprocedure)) > 0)::integer
                """));
            await Run(db, tx, DownSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT (position('COALESCE(NEW."SourceContractId", NEW."TargetContractId")'
                    IN pg_get_functiondef('"BM_GuardFinalizedDepositSettlements"()'::regprocedure)) > 0)::integer
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT (position('ORDER BY c."Id" FOR UPDATE'
                    IN pg_get_functiondef('"BM_GuardFinalizedDepositSettlements"()'::regprocedure)) > 0)::integer
                """));
        });
    }

    private static string OriginalGuardSql()
    {
        var original = Assert.Single(
            new SliceBContractDepositCloseoutApprovals().UpOperations
                .OfType<SqlOperation>()).Sql;
        const string head = "CREATE FUNCTION \"BM_GuardFinalizedDepositSettlements\"()";
        const string tail = "FOR EACH ROW EXECUTE FUNCTION \"BM_GuardFinalizedDepositSettlements\"();";
        var begin = original.IndexOf(head, StringComparison.Ordinal);
        var end = original.IndexOf(tail, begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        return original.Substring(begin, end - begin + tail.Length);
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> probe)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        {
            Pooling = false, CommandTimeout = 25
        }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_settlement_lock_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await Run(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await Run(db, tx, SchemaSql);
                await Run(db, tx, OriginalGuardSql());
                await Run(db, tx, UpSql);
                await probe(db, tx);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await Run(db, tx, "SAVEPOINT settlement_lock_probe");
        string? code = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException error) { code = error.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT settlement_lock_probe");
            await Run(db, tx, "RELEASE SAVEPOINT settlement_lock_probe");
        }
        Assert.Equal("23514", code);
    }

    private static async Task Run(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
