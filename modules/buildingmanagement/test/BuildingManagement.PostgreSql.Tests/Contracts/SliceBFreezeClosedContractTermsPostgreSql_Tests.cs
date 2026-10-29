using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Actual migration SQL in random disposable PostgreSQL schemas only.
/// No production, application data or external financial operations.
/// </summary>
public sealed class SliceBFreezeClosedContractTermsPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new SliceBFreezeClosedContractTerms().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new SliceBFreezeClosedContractTerms().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "ContractNumber" varchar(64) NOT NULL,
            "RoomId" uuid NOT NULL,
            "RenewedFromContractId" uuid NULL,
            "StartDate" date NOT NULL,
            "EndDate" date NULL,
            "MonthlyRent" numeric(18,0) NOT NULL,
            "DepositAmount" numeric(18,0) NOT NULL,
            "Notes" varchar(500) NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE,
            "Status" integer NOT NULL,
            "ActualEndedAt" timestamptz NULL,
            "EndReason" integer NULL,
            "LastModificationTime" timestamptz NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY,
            "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts" ("Id"),
            "ApprovedAtUtc" timestamptz NOT NULL
        );
        """;

    [Fact]
    public async Task SBCTR01_Approval_Preserves_One_Valid_End_And_Financial_History()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            var unrelated = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts"
                  ("Id","ContractNumber","RoomId","StartDate","EndDate",
                   "MonthlyRent","DepositAmount","Notes","Status")
                VALUES ('{contract:D}','CONTRACT-001','{room:D}',
                        '2026-01-01','2026-12-31',1000000,1000000,'Signed lease',2),
                       ('{unrelated:D}','CONTRACT-002','{Guid.NewGuid():D}',
                        '2026-01-01','2026-12-31',1000000,1000000,'Other lease',2);
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","ApprovedAtUtc")
                VALUES ('{Guid.NewGuid():D}','{contract:D}','2026-10-29T03:00:00Z');
                """);

            // ABP may flush audit metadata before invoking Contract.End.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                   SET "LastModificationTime"='2026-10-29T03:00:01Z'
                 WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "DepositAmount"=0
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "MonthlyRent"=5
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "RoomId"='{Guid.NewGuid():D}'
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "IsDeleted"=TRUE
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "ContractNumber"='REWRITTEN'
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "Notes"='Revised terms'
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "Status"=4
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                SET "Status"=3,"ActualEndedAt"='2026-10-29T02:00:00Z',"EndReason"=1
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementContracts" WHERE "Id"='{contract:D}'
                """);

            // The real closeout UoW inserts approval and only THEN ends the
            // Active contract; it must still be legal within one transaction.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                SET "Status"=3,"ActualEndedAt"='2026-10-29T03:00:00Z',"EndReason"=1
                WHERE "Id"='{contract:D}'
                """);
            await Run(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                SET "LastModificationTime"='2026-10-29T03:01:00Z'
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "Status"=2
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "EndReason"=2
                WHERE "Id"='{contract:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                SET "ActualEndedAt"='2026-10-29T03:02:00Z'
                WHERE "Id"='{contract:D}'
                """);

            await Run(db, tx, $"""
                UPDATE "BuildingManagementContracts" SET "MonthlyRent"=2000000
                 WHERE "Id"='{unrelated:D}'
                """);
            Assert.Equal(1000000L, await Scalar(db, tx, $"""
                SELECT "DepositAmount" FROM "BuildingManagementContracts"
                WHERE "Id"='{contract:D}'
                """));
            Assert.Equal(2000000L, await Scalar(db, tx, $"""
                SELECT "MonthlyRent" FROM "BuildingManagementContracts"
                WHERE "Id"='{unrelated:D}'
                """));
            await Denied(db, tx, DownSql);
        });
    }

    [Fact]
    public async Task SBCTR02_Empty_Approval_Allows_Upgrade_Downgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                  AND tgname='TR_BM_ClosedContract_FreezeSource'
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                  AND tgname='TR_BM_ClosedContract_FreezeSource'
                """));
        });
    }

    [Fact]
    public async Task SBCTR03_Concurrent_Modification_Waits_Then_Rejects_Closed_Contract()
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var cs = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false, CommandTimeout = 25
        }.ConnectionString;
        var schema = "bm_contract_freeze_" + Guid.NewGuid().ToString("N");
        var contract = Guid.NewGuid();
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
                INSERT INTO "BuildingManagementContracts"
                  ("Id","ContractNumber","RoomId","StartDate","MonthlyRent",
                   "DepositAmount","Status")
                VALUES ('{contract:D}','CONCURRENT','{Guid.NewGuid():D}',
                        '2026-01-01',1000000,1000000,2);
                """);
            await using var closer = new NpgsqlConnection(cs);
            await using var mutator = new NpgsqlConnection(cs);
            await closer.OpenAsync();
            await mutator.OpenAsync();
            await Run(closer, null, $"SET search_path TO \"{schema}\"");
            await Run(mutator, null, $"SET search_path TO \"{schema}\"");
            await using var closeTx = await closer.BeginTransactionAsync();
            await using var mutateTx = await mutator.BeginTransactionAsync();

            // Real approval INSERT locks Contract FOR UPDATE. Hold its row
            // here until approval is inserted, then commit.
            await Run(closer, closeTx, $"""
                SELECT 1 FROM "BuildingManagementContracts"
                 WHERE "Id"='{contract:D}' FOR UPDATE
                """);
            var pending = Run(mutator, mutateTx, $"""
                UPDATE "BuildingManagementContracts" SET "DepositAmount"=1
                WHERE "Id"='{contract:D}'
                """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            await Run(closer, closeTx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("Id","ContractId","ApprovedAtUtc")
                VALUES ('{Guid.NewGuid():D}','{contract:D}','2026-10-29T03:00:00Z');
                """);
            await closeTx.CommitAsync();

            var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("23514", error.SqlState);
            await mutateTx.RollbackAsync();
            Assert.Equal(1000000L, await Scalar(setup, null,
                "SELECT \"DepositAmount\" FROM \"BuildingManagementContracts\""));
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
        var schema = "bm_contract_freeze_" + Guid.NewGuid().ToString("N");
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
        await Run(db, tx, "SAVEPOINT closed_contract_probe");
        string? code = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException error) { code = error.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT closed_contract_probe");
            await Run(db, tx, "RELEASE SAVEPOINT closed_contract_probe");
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
