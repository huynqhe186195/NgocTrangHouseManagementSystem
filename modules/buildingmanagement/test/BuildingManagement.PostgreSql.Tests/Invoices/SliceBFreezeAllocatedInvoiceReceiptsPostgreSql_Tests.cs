using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;
namespace BuildingManagement.PostgreSqlTests;
/// <summary>Random transaction-local schema, no application database mutations.</summary>
public sealed class SliceBFreezeAllocatedInvoiceReceiptsPostgreSql_Tests
{
    [Fact]
    public async Task SBFRZ01_Journaled_Payment_Is_Immutable_And_Closed_Contract_Is_Frozen()
    {
        await ScratchAsync(async (db, tx, down) =>
        {
            var contract = Guid.NewGuid();
            var payment = Guid.NewGuid();
            var deposit = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Amount","IsDeleted")
                VALUES ('{payment:D}','{contract:D}',3,3083000,FALSE),
                       ('{deposit:D}','{contract:D}',2,3000000,FALSE);
                INSERT INTO "BuildingManagementInvoicePaymentAllocationJournal"
                  ("PaymentId") VALUES ('{payment:D}');
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Amount"=1
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "IsDeleted"=TRUE
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementPayments" WHERE "Id"='{payment:D}'
                """);
            await Denied(db, tx, """TRUNCATE "BuildingManagementPayments" """);
            await Denied(db, tx, down);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  ("ContractId") VALUES ('{contract:D}');
                """);
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Amount","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{contract:D}',3,100,FALSE)
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Purpose"=3
                WHERE "Id"='{deposit:D}'
                """);
            await Denied(db, tx, down);
            Assert.Equal(3083000L, await Scalar(db, tx, $"""
                SELECT "Amount" FROM "BuildingManagementPayments"
                WHERE "Id"='{payment:D}'
                """));
        });
    }
    [Fact]
    public async Task SBFRZ02_Empty_Downgrade_Preserves_Existing_Unlinked_Payment()
    {
        await ScratchAsync(async (db, tx, down) =>
        {
            var contract = Guid.NewGuid();
            var payment = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementPayments"
                  ("Id","ContractId","Purpose","Amount","IsDeleted")
                VALUES ('{payment:D}','{contract:D}',3,123,FALSE);
                """);
            await Run(db, tx, down);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgrelid='"BuildingManagementPayments"'::regclass
                 AND tgname LIKE 'TR_BM_FreezeAllocated%'
                """));
            Assert.Equal(1L, await Scalar(db, tx, $"""
                SELECT count(*) FROM "BuildingManagementPayments"
                WHERE "Id"='{payment:D}'
                """));
        });
    }
    private static string Sql(bool down)
    {
        var m = new SliceBFreezeAllocatedInvoiceReceipts();
        return Assert.Single((down ? m.DownOperations : m.UpOperations)
            .OfType<SqlOperation>()).Sql;
    }
    private static async Task ScratchAsync(
        Func<NpgsqlConnection,NpgsqlTransaction,string,Task> test)
    {
        var source = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(source) { Pooling=false, CommandTimeout=25 }
                .ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_receipt_freeze_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db,tx,$"CREATE SCHEMA \"{schema}\"");
                await Run(db,tx,$"SET LOCAL search_path TO \"{schema}\"");
                await Run(db,tx,"SET LOCAL lock_timeout='3s'");
                await Run(db,tx, """
                    CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
                    CREATE TABLE "BuildingManagementPayments" (
                      "Id" uuid PRIMARY KEY,"ContractId" uuid NULL,
                      "Purpose" integer NOT NULL,"Amount" numeric(18,0) NOT NULL,
                      "IsDeleted" boolean NOT NULL);
                    CREATE TABLE "BuildingManagementInvoicePaymentAllocationJournal"
                      ("PaymentId" uuid NOT NULL);
                    CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals"
                      ("ContractId" uuid NOT NULL);
                    """);
                await Run(db,tx,Sql(false));
                await test(db,tx,Sql(true));
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }
    private static async Task Denied(NpgsqlConnection db,
        NpgsqlTransaction tx,string sql)
    {
        const string name="receipt_probe";
        await Run(db,tx,"SAVEPOINT "+name);
        string? code=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException error){code=error.SqlState;}
        finally {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT "+name);
            await Run(db,tx,"RELEASE SAVEPOINT "+name);
        }
        Assert.Equal("23514",code);
    }
    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
