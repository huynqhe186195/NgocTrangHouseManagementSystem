using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>Exact SQL in transaction-rollback scratch schemas only.</summary>
public sealed class SliceBFreezeClosedMoveOutChecklistPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBFreezeClosedMoveOutChecklist().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBFreezeClosedMoveOutChecklist().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY,
            "ContractId" uuid NOT NULL,
            "MoveOutRequestId" uuid NOT NULL,
            "ApprovedAtUtc" timestamptz NOT NULL
        );
        CREATE TABLE "BuildingManagementMoveOutChecklistTasks" (
            "Id" uuid PRIMARY KEY,
            "MoveOutRequestId" uuid NOT NULL,
            "Type" integer NOT NULL,
            "IsCompleted" boolean NOT NULL,
            "CompletedAt" timestamptz NULL,
            "LastModificationTime" timestamptz NULL
        );
        """;
    private const string ApprovedAt = "2026-10-29T03:00:00Z";

    [Fact]
    public async Task SBCL01_Approval_Requires_All_Five_But_Allows_Atomic_Signoff()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var contract = Guid.NewGuid();
            var request = Guid.NewGuid();
            await Seed(db, tx, contract, request);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}','{ApprovedAt}');
                """);
            await Denied(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireChecklistAtCommit\" IMMEDIATE",
                "23514");

            // The live ABP UoW may flush audit-only changes while the
            // four non-meter tasks remain incomplete after approval insert.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "LastModificationTime"='2026-10-29T03:00:01Z'
                 WHERE "MoveOutRequestId"='{request:D}';
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "IsCompleted"=FALSE,"CompletedAt"=NULL
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=0
                ""","23514");
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "Type"=7
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=1
                ""","23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementMoveOutChecklistTasks"
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=3
                ""","23514");
            await Denied(db, tx,
                """TRUNCATE "BuildingManagementMoveOutChecklistTasks" ""","23514");
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementMoveOutChecklistTasks"
                VALUES ('{Guid.NewGuid():D}','{request:D}',8,FALSE,NULL,NULL)
                ""","23514");
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "IsCompleted"=TRUE,"CompletedAt"='2026-10-29T02:00:00Z'
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=1
                ""","23514");

            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "IsCompleted"=TRUE,"CompletedAt"='{ApprovedAt}'
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type" BETWEEN 1 AND 4;
                """);
            await Run(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireChecklistAtCommit\" IMMEDIATE");
            Assert.Equal(5L, await Scalar(db, tx, $"""
                SELECT count(*) FROM "BuildingManagementMoveOutChecklistTasks"
                 WHERE "MoveOutRequestId"='{request:D}' AND "IsCompleted"
                """));
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "CompletedAt"='2026-10-29T04:00:00Z'
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=4
                ""","23514");
            await Denied(db, tx, DownSql, "23514");
        });
    }

    [Fact]
    public async Task SBCL02_Empty_History_Downgrade_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname LIKE 'TR_BM_ClosedChecklist%'
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(2L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname LIKE 'TR_BM_ClosedChecklist%'
                """));
        });
    }

    [Fact]
    public async Task SBCL03_Concurrent_Edit_After_Approval_Is_Denied()
    {
        var source=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var cs=new NpgsqlConnectionStringBuilder(source)
            { Pooling=false, CommandTimeout=25 }.ConnectionString;
        var schema="bm_checklist_freeze_"+Guid.NewGuid().ToString("N");
        var contract=Guid.NewGuid();
        var request=Guid.NewGuid();
        await using var setup=new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,setup.Database);
        try
        {
            await Run(setup,null,$"CREATE SCHEMA \"{schema}\"");
            await Run(setup,null,$"SET search_path TO \"{schema}\"");
            await Run(setup,null,SchemaSql);
            await Run(setup,null,UpSql);
            await Seed(setup,null,contract,request);
            await using var closer=new NpgsqlConnection(cs);
            await using var editor=new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await editor.OpenAsync();
            await Run(closer,null,$"SET search_path TO \"{schema}\"");
            await Run(editor,null,$"SET search_path TO \"{schema}\"");
            await using var cTx=await closer.BeginTransactionAsync();
            await using var eTx=await editor.BeginTransactionAsync();

            await Run(closer,cTx,$"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}','{ApprovedAt}');
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "IsCompleted"=TRUE,"CompletedAt"='{ApprovedAt}'
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type" BETWEEN 1 AND 4;
                """);
            var pending=Run(editor,eTx,$"""
                UPDATE "BuildingManagementMoveOutChecklistTasks"
                   SET "IsCompleted"=FALSE,"CompletedAt"=NULL
                 WHERE "MoveOutRequestId"='{request:D}' AND "Type"=0
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            await cTx.CommitAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>pending);
            Assert.Equal("23514",error.SqlState);
            await eTx.RollbackAsync();
            Assert.Equal(5L,await Scalar(setup,null,$"""
                SELECT count(*) FROM "BuildingManagementMoveOutChecklistTasks"
                 WHERE "MoveOutRequestId"='{request:D}' AND "IsCompleted"
                """));
        }
        finally
        {
            await Run(setup,null,"RESET search_path");
            await Run(setup,null,$"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L,await Scalar(setup,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Seed(
        NpgsqlConnection db,NpgsqlTransaction? tx,Guid contract,Guid request)
    {
        await Run(db,tx,$"""
            INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
            INSERT INTO "BuildingManagementMoveOutRequests" VALUES ('{request:D}','{contract:D}');
            INSERT INTO "BuildingManagementMoveOutChecklistTasks"
                ("Id","MoveOutRequestId","Type","IsCompleted","CompletedAt")
            VALUES
                ('{Guid.NewGuid():D}','{request:D}',0,TRUE,'2026-10-29T02:00:00Z'),
                ('{Guid.NewGuid():D}','{request:D}',1,FALSE,NULL),
                ('{Guid.NewGuid():D}','{request:D}',2,FALSE,NULL),
                ('{Guid.NewGuid():D}','{request:D}',3,FALSE,NULL),
                ('{Guid.NewGuid():D}','{request:D}',4,FALSE,NULL);
            """);
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> probe)
    {
        var source=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(source)
                { Pooling=false,CommandTimeout=25 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_checklist_freeze_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db,tx,$"CREATE SCHEMA \"{schema}\"");
                await Run(db,tx,$"SET LOCAL search_path TO \"{schema}\"");
                await Run(db,tx,SchemaSql);
                await Run(db,tx,UpSql);
                await probe(db,tx);
            }
            finally {await tx.RollbackAsync();}
        }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT checklist_freeze_probe");
        string? observed=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException error){observed=error.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT checklist_freeze_probe");
            await Run(db,tx,"RELEASE SAVEPOINT checklist_freeze_probe");
        }
        Assert.Equal(state,observed);
    }

    private static async Task Run(
        NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(
        NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
