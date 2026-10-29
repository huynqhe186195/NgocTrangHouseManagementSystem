using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real source-only SQL, isolated random PostgreSQL schema and rollback.
/// </summary>
public sealed class SliceBFreezeClosedContractPartiesAndExtrasPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBFreezeClosedContractPartiesAndExtras().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBFreezeClosedContractPartiesAndExtras().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid PRIMARY KEY, "ExtraProperties" text NOT NULL DEFAULT '{}',
            "LastModificationTime" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "ExtraProperties" text NOT NULL DEFAULT '{}'
        );
        CREATE TABLE "BuildingManagementMoveOutChecklistTasks" (
            "Id" uuid PRIMARY KEY, "MoveOutRequestId" uuid NOT NULL,
            "ExtraProperties" text NOT NULL DEFAULT '{}'
        );
        CREATE TABLE "BuildingManagementContractTenants" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "TenantId" uuid NOT NULL, "Role" integer NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE,
            "LastModificationTime" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "MoveOutRequestId" uuid NOT NULL
        );
        """;

    [Fact]
    public async Task SBPART01_Signed_Membership_And_Extended_Properties_Are_Frozen()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var contract = Guid.NewGuid();
            var other = Guid.NewGuid();
            var request = Guid.NewGuid();
            var task = Guid.NewGuid();
            var party = Guid.NewGuid();
            await Seed(db, tx, contract, other, request, task, party);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}');
                """);
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","TenantId","Role")
                VALUES ('{Guid.NewGuid():D}','{contract:D}','{Guid.NewGuid():D}',1)
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContractTenants" SET "Role"=4
                 WHERE "Id"='{party:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContractTenants"
                   SET "TenantId"='{Guid.NewGuid():D}'
                 WHERE "Id"='{party:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContractTenants"
                   SET "ContractId"='{other:D}'
                 WHERE "Id"='{party:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContractTenants"
                   SET "IsDeleted"=TRUE WHERE "Id"='{party:D}'
                """);
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementContractTenants"
                 WHERE "Id"='{party:D}'
                """);
            await Denied(db, tx,
                """TRUNCATE "BuildingManagementContractTenants" """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                   SET "ExtraProperties"=json_build_object('terms', 'rewritten')::text
                 WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "ExtraProperties"=json_build_object('proof', 'replaced')::text
                 WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "ExtraProperties"=json_build_object('verified', FALSE)::text
                 WHERE "Id"='{task:D}'
                """);

            // ABP concurrency/audit bookkeeping is still allowed.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementContractTenants"
                   SET "LastModificationTime"='2026-10-29T12:00:00Z'
                 WHERE "Id"='{party:D}';
                UPDATE "BuildingManagementContracts"
                   SET "LastModificationTime"='2026-10-29T12:00:00Z'
                 WHERE "Id"='{contract:D}';
                """);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","TenantId","Role")
                VALUES ('{Guid.NewGuid():D}','{other:D}','{Guid.NewGuid():D}',1);
                UPDATE "BuildingManagementContracts"
                   SET "ExtraProperties"=json_build_object('open', TRUE)::text
                 WHERE "Id"='{other:D}';
                """);
            Assert.Equal(1L, await Scalar(db, tx, $"""
                SELECT count(*) FROM "BuildingManagementContractTenants"
                 WHERE "Id"='{party:D}' AND "IsDeleted"=FALSE
                """));
            await Denied(db, tx, DownSql);
        });
    }

    [Fact]
    public async Task SBPART02_Empty_History_Downgrades_Without_Dropping_Existing_Tables()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND (tgname LIKE 'TR_BM_Signed%')
                """));
            Assert.Equal(1L, await Scalar(db, tx,
                "SELECT to_regclass('\"BuildingManagementContractTenants\"') IS NOT NULL"));
            await Run(db, tx, UpSql);
            Assert.Equal(5L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND (tgname LIKE 'TR_BM_Signed%')
                """));
        });
    }

    [Fact]
    public async Task SBPART03_Concurrent_Tenant_Edit_Waits_For_Signed_Approval()
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            { Pooling = false, CommandTimeout = 25 }.ConnectionString;
        var schema = "bm_parties_" + Guid.NewGuid().ToString("N");
        var contract = Guid.NewGuid();
        var request = Guid.NewGuid();
        var party = Guid.NewGuid();
        await using var setup = new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, setup.Database);
        try
        {
            await Run(setup, null, $"CREATE SCHEMA \"{schema}\"");
            await Run(setup, null, $"SET search_path TO \"{schema}\"");
            await Run(setup, null, SchemaSql);
            await Run(setup, null, UpSql);
            await Seed(setup, null, contract, Guid.NewGuid(), request,
                Guid.NewGuid(), party);
            await using var closer = new NpgsqlConnection(cs);
            await using var editor = new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await editor.OpenAsync();
            await Run(closer, null, $"SET search_path TO \"{schema}\"");
            await Run(editor, null, $"SET search_path TO \"{schema}\"");
            await using var closeTx = await closer.BeginTransactionAsync();
            await using var editTx = await editor.BeginTransactionAsync();
            await Run(closer, closeTx, $"""
                SELECT 1 FROM "BuildingManagementContracts"
                 WHERE "Id"='{contract:D}' FOR UPDATE
                """);
            var pending = Run(editor, editTx, $"""
                UPDATE "BuildingManagementContractTenants"
                   SET "Role"=4 WHERE "Id"='{party:D}'
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            await Run(closer, closeTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}');
                """);
            await closeTx.CommitAsync();
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23514", rejected.SqlState);
            await editTx.RollbackAsync();
            Assert.Equal(1L, await Scalar(setup, null, $"""
                SELECT "Role" FROM "BuildingManagementContractTenants"
                 WHERE "Id"='{party:D}'
                """));
        }
        finally
        {
            await Run(setup, null, "RESET search_path");
            await Run(setup, null, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L, await Scalar(setup, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Seed(NpgsqlConnection db, NpgsqlTransaction? tx,
        Guid contract, Guid unrelated, Guid request, Guid task, Guid party)
    {
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementContracts" ("Id")
                VALUES ('{contract:D}'),('{unrelated:D}');
            INSERT INTO "BuildingManagementMoveOutRequests" ("Id","ContractId")
                VALUES ('{request:D}','{contract:D}');
            INSERT INTO "BuildingManagementMoveOutChecklistTasks"
                ("Id","MoveOutRequestId") VALUES ('{task:D}','{request:D}');
            INSERT INTO "BuildingManagementContractTenants"
                ("Id","ContractId","TenantId","Role")
                VALUES ('{party:D}','{contract:D}','{Guid.NewGuid():D}',1);
            """);
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> test)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            { Pooling = false, CommandTimeout = 25 }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_parties_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await Run(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await Run(db, tx, SchemaSql);
                await Run(db, tx, UpSql);
                await test(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db, NpgsqlTransaction tx,
        string sql)
    {
        await Run(db, tx, "SAVEPOINT contract_parties_probe");
        string? state = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException e) { state = e.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT contract_parties_probe");
            await Run(db, tx, "RELEASE SAVEPOINT contract_parties_probe");
        }
        Assert.Equal("23514", state);
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
