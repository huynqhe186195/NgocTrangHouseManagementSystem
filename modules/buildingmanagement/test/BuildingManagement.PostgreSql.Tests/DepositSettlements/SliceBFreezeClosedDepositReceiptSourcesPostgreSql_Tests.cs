using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>Exact migration SQL on randomly named scratch PostgreSQL schemas.</summary>
public sealed class SliceBFreezeClosedDepositReceiptSourcesPostgreSql_Tests
{
    private static readonly string UpSql=Assert.Single(
        new SliceBFreezeClosedDepositReceiptSources().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql=Assert.Single(
        new SliceBFreezeClosedDepositReceiptSources().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementPayments" (
            "Id" uuid PRIMARY KEY,
            "ContractId" uuid NULL, "RoomReservationId" uuid NULL,
            "Purpose" integer NOT NULL, "Status" integer NOT NULL,
            "Amount" numeric(18,0) NOT NULL, "PaidAt" timestamptz NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE
        );
        CREATE TABLE "BuildingManagementDepositSettlements" (
            "Id" uuid PRIMARY KEY,"Type" integer NOT NULL,
            "Status" integer NOT NULL,"TargetContractId" uuid NULL,
            "SourceRoomReservationId" uuid NULL,"PaymentId" uuid NULL,
            "Amount" numeric(18,0) NOT NULL
        );
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
            "Id" uuid PRIMARY KEY,"ContractId" uuid NOT NULL
        );
        """;

    [Fact]
    public async Task SBDEP01_Valid_Direct_And_Reservation_Receipts_Freeze_After_Closeout()
    {
        await ScratchAsync(async (db,tx)=>
        {
            var contract=Guid.NewGuid();
            var other=Guid.NewGuid();
            var direct=Guid.NewGuid();
            var res=Guid.NewGuid();
            var reservation=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}'),('{other:D}');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Status","Amount","PaidAt")
                VALUES ('{direct:D}','{contract:D}',2,2,2000000,'2026-10-29T03:00:00Z');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","RoomReservationId","Purpose","Status","Amount","PaidAt")
                VALUES ('{res:D}','{reservation:D}',1,2,2000000,'2026-10-29T03:00:00Z');
                INSERT INTO "BuildingManagementDepositSettlements"
                  ("Id","Type","Status","TargetContractId",
                   "SourceRoomReservationId","PaymentId","Amount")
                VALUES ('{Guid.NewGuid():D}',3,2,'{contract:D}',NULL,'{direct:D}',2000000),
                       ('{Guid.NewGuid():D}',3,2,'{contract:D}','{reservation:D}','{res:D}',1000000);
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}');
                """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "Amount"=1
                WHERE "Id"='{direct:D}' """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "Status"=1
                WHERE "Id"='{res:D}' """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "IsDeleted"=TRUE
                WHERE "Id"='{direct:D}' """);
            await Denied(db,tx,$"""
                DELETE FROM "BuildingManagementPayments" WHERE "Id"='{res:D}' """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Status","Amount")
                VALUES ('{Guid.NewGuid():D}','{contract:D}',2,1,1) """);
            await Denied(db,tx,DownSql);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Status","Amount")
                VALUES ('{Guid.NewGuid():D}','{other:D}',2,1,1);
                UPDATE "BuildingManagementPayments" SET "Amount"=1500000
                WHERE "Id" IN (
                    SELECT "PaymentId" FROM "BuildingManagementDepositSettlements"
                     WHERE "TargetContractId"='{contract:D}' AND "Amount"=1000000
                ) AND FALSE;
                """);
            Assert.Equal(2000000L,await Scalar(db,tx,$"""
                SELECT "Amount" FROM "BuildingManagementPayments"
                 WHERE "Id"='{direct:D}' """));
        });
    }

    [Fact]
    public async Task SBDEP02_Invalid_Receipt_Sources_Reject_Approval_Without_Moving_Money()
    {
        await ScratchAsync(async (db,tx)=>
        {
            var contract=Guid.NewGuid();
            var payment=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Status","Amount","PaidAt")
                VALUES ('{payment:D}','{contract:D}',2,1,1000000,NULL);
                INSERT INTO "BuildingManagementDepositSettlements"
                  ("Id","Type","Status","TargetContractId","PaymentId","Amount")
                VALUES ('{Guid.NewGuid():D}',3,2,'{contract:D}','{payment:D}',1000000);
                """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}'')
                """.Replace("''", "'"));
            await Run(db,tx,$"""
                UPDATE "BuildingManagementPayments"
                SET "Status"=2,"PaidAt"='2026-10-29T03:00:00Z'
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "Amount"=1
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}')
                """);
            await Run(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "Amount"=1000000
                WHERE "Id"='{payment:D}';
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}');
                """);
            Assert.Equal(1L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositCloseoutApprovals" """));
        });
    }

    [Fact]
    public async Task SBDEP03_Empty_Downgrade_And_Reupgrade_Safe()
    {
        await ScratchAsync(async (db,tx)=>
        {
            await Run(db,tx,DownSql);
            Assert.Equal(0L,await Scalar(db,tx,"""
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_ClosedDepositReceipt_SourceFreeze'
                """));
            await Run(db,tx,UpSql);
        });
    }

    [Fact]
    public async Task SBDEP04_Competing_Receipt_Edit_Waits_For_Closeout_Commit()
    {
        var cs=new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            {Pooling=false,CommandTimeout=25}.ConnectionString;
        var schema="bm_deposit_receipts_"+Guid.NewGuid().ToString("N");
        var contract=Guid.NewGuid();
        var payment=Guid.NewGuid();
        await using var setup=new NpgsqlConnection(cs);
        await setup.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,setup.Database);
        try
        {
            await Run(setup,null,$"CREATE SCHEMA \"{schema}\"");
            await Run(setup,null,$"SET search_path TO \"{schema}\"");
            await Run(setup,null,SchemaSql);
            await Run(setup,null,UpSql);
            await Run(setup,null,$"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Status","Amount","PaidAt")
                VALUES ('{payment:D}','{contract:D}',2,2,1000000,'2026-10-29T03:00:00Z');
                INSERT INTO "BuildingManagementDepositSettlements"
                  ("Id","Type","Status","TargetContractId","PaymentId","Amount")
                VALUES ('{Guid.NewGuid():D}',3,2,'{contract:D}','{payment:D}',1000000);
                """);
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
                 VALUES ('{Guid.NewGuid():D}','{contract:D}');
                """);
            var pending=Run(editor,eTx,$"""
                UPDATE "BuildingManagementPayments" SET "Amount"=1
                 WHERE "Id"='{payment:D}' """);
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            await cTx.CommitAsync();
            var rejected=await Assert.ThrowsAsync<PostgresException>(()=>pending);
            Assert.Equal("23514",rejected.SqlState);
            await eTx.RollbackAsync();
            Assert.Equal(1000000L,await Scalar(setup,null,
                """SELECT "Amount" FROM "BuildingManagementPayments" """));
        }
        finally
        {
            await Run(setup,null,"RESET search_path");
            await Run(setup,null,$"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
        Assert.Equal(0L,await Scalar(setup,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> body)
    {
        var cs=new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            {Pooling=false,CommandTimeout=25}.ConnectionString;
        await using var db=new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_deposit_receipts_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db,tx,$"CREATE SCHEMA \"{schema}\"");
                await Run(db,tx,$"SET LOCAL search_path TO \"{schema}\"");
                await Run(db,tx,SchemaSql);
                await Run(db,tx,UpSql);
                await body(db,tx);
            }
            finally {await tx.RollbackAsync();}
        }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await Run(db,tx,"SAVEPOINT deposit_receipt_probe");
        string? state=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException e){state=e.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT deposit_receipt_probe");
            await Run(db,tx,"RELEASE SAVEPOINT deposit_receipt_probe");
        }
        Assert.Equal("23514",state);
    }

    private static async Task Run(NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
