using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>Real migration SQL in a disposable PostgreSQL schema only.</summary>
public sealed class SliceBFreezeClosedMoveOutRequestPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBFreezeClosedMoveOutRequest().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBFreezeClosedMoveOutRequest().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY,
            "ContractId" uuid NOT NULL,
            "RoomId" uuid NOT NULL,
            "RequestedMoveOutDate" date NOT NULL,
            "ApprovedMoveOutDate" date NULL,
            "Reason" varchar(1024) NULL,
            "ReviewNotes" varchar(1024) NULL,
            "ReviewedAt" timestamptz NULL,
            "CancelledAt" timestamptz NULL,
            "CompletedAt" timestamptz NULL,
            "Status" integer NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE,
            "LastModificationTime" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY,
            "MoveOutRequestId" uuid NOT NULL
                REFERENCES "BuildingManagementMoveOutRequests" ("Id"),
            "ApprovedAtUtc" timestamptz NOT NULL
        );
        """;

    [Fact]
    public async Task SBMOFR01_Approval_Allows_Completion_But_Denies_History_Rewrites()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var request = Guid.NewGuid();
            var unrelated = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementMoveOutRequests"
                  ("Id","ContractId","RoomId","RequestedMoveOutDate",
                   "ApprovedMoveOutDate","Reason","ReviewNotes","ReviewedAt","Status")
                VALUES ('{request:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}',
                        '2026-10-28','2026-10-28','Move','Approved',
                        '2026-10-28T02:00:00Z',1),
                       ('{unrelated:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}',
                        '2026-10-30',NULL,'Move',NULL,NULL,0);
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","MoveOutRequestId","ApprovedAtUtc")
                VALUES ('{Guid.NewGuid():D}','{request:D}',
                        '2026-10-29T03:00:00Z');
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "ContractId"='{Guid.NewGuid():D}' WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "RoomId"='{Guid.NewGuid():D}' WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "ApprovedMoveOutDate"='2026-12-31' WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Reason"='Rewritten cause' WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "IsDeleted"=TRUE WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=3 WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=4,"CompletedAt"='2026-10-29T02:00:00Z'
                 WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementMoveOutRequests" WHERE "Id"='{request:D}'
                """);

            // Actual root closeout finishes this request AFTER approval.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=4,"CompletedAt"='2026-10-29T03:00:00Z'
                 WHERE "Id"='{request:D}'
                """);
            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "LastModificationTime"='2026-10-29T03:01:00Z'
                 WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "CompletedAt"='2026-10-29T03:02:00Z'
                 WHERE "Id"='{request:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=1 WHERE "Id"='{request:D}'
                """);

            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "RequestedMoveOutDate"='2026-11-01'
                 WHERE "Id"='{unrelated:D}'
                """);
            Assert.Equal(4L, await Scalar(db, tx, $"""
                SELECT "Status" FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id"='{request:D}'
                """));
            await Denied(db, tx, DownSql);
        });
    }

    [Fact]
    public async Task SBMOFR02_Empty_Closeout_Allows_Downgrade_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_ClosedMoveOut_FreezeSource'
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_ClosedMoveOut_FreezeSource'
                """));
        });
    }

    [Fact]
    public async Task SBMOFR03_Concurrent_Request_Edit_Waits_For_Signed_Closeout()
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        { Pooling = false, CommandTimeout = 25 }.ConnectionString;
        var schema = "bm_moveout_freeze_" + Guid.NewGuid().ToString("N");
        var request = Guid.NewGuid();
        await using var setup = new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, setup.Database);
        try
        {
            await Run(setup, null, $"CREATE SCHEMA \"{schema}\"");
            await Run(setup, null, $"SET search_path TO \"{schema}\"");
            await Run(setup, null, SchemaSql);
            await Run(setup, null, UpSql);
            await Run(setup, null, $"""
                INSERT INTO "BuildingManagementMoveOutRequests"
                  ("Id","ContractId","RoomId","RequestedMoveOutDate",
                   "ApprovedMoveOutDate","Status")
                VALUES ('{request:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}',
                        '2026-10-28','2026-10-28',1);
                """);
            await using var closer = new NpgsqlConnection(cs);
            await using var editor = new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await editor.OpenAsync();
            await Run(closer, null, $"SET search_path TO \"{schema}\"");
            await Run(editor, null, $"SET search_path TO \"{schema}\"");
            await using var closeTx = await closer.BeginTransactionAsync();
            await using var editTx = await editor.BeginTransactionAsync();
            await Run(closer, closeTx, $"""
                SELECT 1 FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id"='{request:D}' FOR UPDATE
                """);
            var pending = Run(editor, editTx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "ApprovedMoveOutDate"='2026-11-30'
                 WHERE "Id"='{request:D}'
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            await Run(closer, closeTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","MoveOutRequestId","ApprovedAtUtc")
                VALUES ('{Guid.NewGuid():D}','{request:D}','2026-10-29T03:00:00Z');
                """);
            await closeTx.CommitAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23514", error.SqlState);
            await editTx.RollbackAsync();
            Assert.Equal(1L, await Scalar(setup, null,
                "SELECT \"Status\" FROM \"BuildingManagementMoveOutRequests\""));
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
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        { Pooling = false, CommandTimeout = 25 }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_moveout_freeze_" + Guid.NewGuid().ToString("N");
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
        await Run(db, tx, "SAVEPOINT moveout_freeze_probe");
        string? code = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException error) { code = error.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT moveout_freeze_probe");
            await Run(db, tx, "RELEASE SAVEPOINT moveout_freeze_probe");
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
