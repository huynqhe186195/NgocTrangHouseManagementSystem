using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>Transaction-local scratch schema, no persistent application data.</summary>
public sealed class SliceBValidateCompletedCloseoutAtCommitPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBValidateCompletedCloseoutAtCommit().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBValidateCompletedCloseoutAtCommit().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid PRIMARY KEY, "Status" integer NOT NULL,
            "ActualEndedAt" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "Status" integer NOT NULL, "CompletedAt" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "MoveOutRequestId" uuid NOT NULL,
            "ApprovedAtUtc" timestamptz NOT NULL
        );
        """;

    [Fact]
    public async Task SBEND01_Intermediate_Active_Approved_Allowed_But_Not_At_Commit()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var contract = Guid.NewGuid();
            var request = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}',2,NULL);
                INSERT INTO "BuildingManagementMoveOutRequests"
                    VALUES ('{request:D}','{contract:D}',1,NULL);
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                    VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
                            '2026-10-29T03:00:00Z');
                """);
            // Deferred while app saves approval and audit metadata.
            await Denied(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireCompletedAtCommit\" IMMEDIATE");

            // Even ending only one of the two sources cannot commit.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                   SET "Status"=3,"ActualEndedAt"='2026-10-29T03:00:00Z'
                 WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireCompletedAtCommit\" IMMEDIATE");

            // This is the actual app transaction's legal final state.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=4,"CompletedAt"='2026-10-29T03:00:00Z'
                 WHERE "Id"='{request:D}'
                """);
            await Run(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireCompletedAtCommit\" IMMEDIATE");
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM "BuildingManagementContractDepositCloseoutApprovals"
                """));
            await Denied(db, tx, DownSql);
        });
    }

    [Fact]
    public async Task SBEND02_Cannot_Attest_Foreign_Request_Or_Before_Approval_Time()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var contract = Guid.NewGuid();
            var other = Guid.NewGuid();
            var request = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts"
                   VALUES ('{contract:D}',3,'2026-10-29T03:00:00Z'),
                          ('{other:D}',3,'2026-10-29T03:00:00Z');
                INSERT INTO "BuildingManagementMoveOutRequests"
                   VALUES ('{request:D}','{other:D}',4,'2026-10-29T02:59:59Z');
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                   VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
                           '2026-10-29T03:00:00Z');
                """);
            await Denied(db, tx,
                "SET CONSTRAINTS \"TR_BM_DepositCloseout_RequireCompletedAtCommit\" IMMEDIATE");
        });
    }

    [Fact]
    public async Task SBEND03_No_Closeout_Downgrades_Cleanly()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_DepositCloseout_RequireCompletedAtCommit'
                """));
        });
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> probe)
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(source)
            { Pooling=false, CommandTimeout=25 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema="bm_end_commit_"+Guid.NewGuid().ToString("N");
        await using (var tx=await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db,tx,$"CREATE SCHEMA \"{schema}\"");
                await Run(db,tx,$"SET LOCAL search_path TO \"{schema}\"");
                await Run(db,tx,SchemaSql);
                await Run(db,tx,UpSql);
                await probe(db,tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await Run(db,tx,"SAVEPOINT closeout_end_probe");
        string? code=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException error){code=error.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT closeout_end_probe");
            await Run(db,tx,"RELEASE SAVEPOINT closeout_end_probe");
        }
        Assert.Equal("23514",code);
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
