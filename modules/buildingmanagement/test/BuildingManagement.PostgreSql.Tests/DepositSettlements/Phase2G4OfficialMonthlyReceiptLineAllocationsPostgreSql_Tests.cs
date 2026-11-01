using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class Phase2G4OfficialMonthlyReceiptLineAllocationsPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2G4OfficialMonthlyReceiptLineAllocations().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2G4OfficialMonthlyReceiptLineAllocations().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Fixtures = """
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables"(
          "Id" uuid PRIMARY KEY,"Kind" integer NOT NULL,
          "IssuedInvoiceId" uuid NOT NULL,"ContractId" uuid NOT NULL,
          "SourceEvidenceSha256" varchar(64) NOT NULL);
        CREATE TABLE "BuildingManagementOfficialInvoiceReceiptReviews"(
          "Id" uuid PRIMARY KEY,"ReceivablePostingId" uuid NOT NULL,
          "ReceiptAllocationId" uuid NOT NULL,"PaymentId" uuid NOT NULL,
          "ContractId" uuid NOT NULL,"Amount" numeric(18,0) NOT NULL,
          "Accepted" boolean NOT NULL,"ReviewedByUserId" uuid NOT NULL,
          "ReviewedAtUtc" timestamptz NOT NULL);
        CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates"(
          "Id" uuid PRIMARY KEY,"ContractId" uuid NOT NULL,
          "FrozenEvidenceSha256" varchar(64) NOT NULL,
          "RentAmount" numeric(18,0) NOT NULL,
          "ElectricityAmount" numeric(18,0) NOT NULL,
          "WaterAmount" numeric(18,0) NOT NULL,
          "AuxiliaryAmount" numeric(18,0) NOT NULL);
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY,"ContractId" uuid NOT NULL,
          "CandidateId" uuid NOT NULL);
        CREATE TABLE "BuildingManagementMonthlyInvoicePaymentAllocation"(
          "Id" uuid PRIMARY KEY,"PaymentId" uuid NOT NULL,
          "IssuedInvoiceId" uuid NOT NULL,"ContractId" uuid NOT NULL,
          "Amount" numeric(18,0) NOT NULL);
        """;
    private static readonly Guid Posting = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Contract = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid Issued = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid Candidate = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid Review = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private static readonly Guid Receipt = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
    private static readonly Guid Payment = Guid.Parse("99999999-9999-4999-8999-999999999999");
    private static readonly Guid BankReviewer = Guid.Parse("88888888-8888-4888-8888-888888888888");
    private static readonly Guid SplitReviewer = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly string Hash = new('a', 64);

    private static string Insert(int category, int amount, Guid? actor = null) => $"""
        INSERT INTO "BuildingManagementOfficialMonthlyReceiptLineAllocations" (
          "Id","ReceivablePostingId","ReceiptReviewId","ReceiptAllocationId",
          "PaymentId","ContractId","Category","Amount","AllocatedByUserId",
          "AllocatedAtUtc","SignedAllocationReference","InvoiceEvidenceSha256"
        ) VALUES ('{Guid.NewGuid():D}','{Posting:D}','{Review:D}','{Receipt:D}',
                  '{Payment:D}','{Contract:D}',{category},{amount},
                  '{actor ?? SplitReviewer:D}',now(),
                  'SIGNED-INDEPENDENT-CATEGORY-ALLOCATION','{Hash}');
        """;

    [Fact]
    public async Task OI04SQL01_Complete_Signed_Split_Commits_And_History_Is_Frozen()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,Insert(1,200000));
            await Run(db,tx,Insert(2,35000));
            await Run(db,tx,Insert(3,15000));
            await Run(db,tx,"SET CONSTRAINTS \"TR_BM_VerifyOfficialMonthlyReceiptLineCommit\" IMMEDIATE");
            Assert.Equal(250000L,await Scalar(db,tx,"""
                SELECT sum("Amount") FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                """));
            await Denied(db,tx,"""
                UPDATE "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                SET "Amount"=1
                ""","23514");
            await Denied(db,tx,"""
                DELETE FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                ""","23514");
            await Denied(db,tx,Insert(2,1),"23505");
            await Denied(db,tx,DownSql,"23514");
        });
    }

    [Fact]
    public async Task OI04SQL02_Incomplete_Allocation_Fails_At_Deferred_Commit_Boundary()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,Insert(1,200000));
            await Denied(db,tx,
                "SET CONSTRAINTS \"TR_BM_VerifyOfficialMonthlyReceiptLineCommit\" IMMEDIATE",
                "23514");
        }, completed: false);
    }

    [Fact]
    public async Task OI04SQL03_Over_Frozen_Charge_And_SelfReview_Are_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            await Denied(db,tx,Insert(2,40000),"23514");
            await Denied(db,tx,Insert(1,250000,BankReviewer),"23514");
            await Denied(db,tx,Insert(4,1),"23514");
        }, completed: false);
    }

    [Fact]
    public async Task OI04SQL04_Empty_History_Allows_Downgrade_And_Reupgrade()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,DownSql);
            Assert.Equal(0L,await Scalar(db,tx,"""
                SELECT count(*) FROM pg_tables
                 WHERE schemaname=current_schema()
                   AND tablename='BuildingManagementOfficialMonthlyReceiptLineAllocations'
                """));
            await Run(db,tx,UpSql);
        }, completed: false);
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> run, bool completed = true)
    {
        var source=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(source)
        { Pooling=false,CommandTimeout=30 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        string schema="bm_oi04_lines_"+Guid.NewGuid().ToString("N");
        await using var tx=await db.BeginTransactionAsync();
        try
        {
            await Run(db,tx,$"CREATE SCHEMA \"{schema}\"");
            await Run(db,tx,$"SET LOCAL search_path TO \"{schema}\"");
            await Run(db,tx,Fixtures);
            await Run(db,tx,UpSql);
            if (completed)
            {
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                    ('{Candidate:D}','{Contract:D}','{Hash}',3000000,35000,15000,0);
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                    ('{Issued:D}','{Contract:D}','{Candidate:D}');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                    ('{Posting:D}',1,'{Issued:D}','{Contract:D}','{Hash}');
                    INSERT INTO "BuildingManagementMonthlyInvoicePaymentAllocation" VALUES
                    ('{Receipt:D}','{Payment:D}','{Issued:D}','{Contract:D}',250000);
                    INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews" VALUES
                    ('{Review:D}','{Posting:D}','{Receipt:D}','{Payment:D}',
                     '{Contract:D}',250000,TRUE,'{BankReviewer:D}',now()-interval '1 day');
                    """);
            }
            else
            {
                // DDL-only probe and incomplete split use the same safe
                // isolated source rows; each failure rolls back to savepoint.
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                    ('{Candidate:D}','{Contract:D}','{Hash}',3000000,35000,15000,0);
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                    ('{Issued:D}','{Contract:D}','{Candidate:D}');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                    ('{Posting:D}',1,'{Issued:D}','{Contract:D}','{Hash}');
                    INSERT INTO "BuildingManagementMonthlyInvoicePaymentAllocation" VALUES
                    ('{Receipt:D}','{Payment:D}','{Issued:D}','{Contract:D}',250000);
                    INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews" VALUES
                    ('{Review:D}','{Posting:D}','{Receipt:D}','{Payment:D}',
                     '{Contract:D}',250000,TRUE,'{BankReviewer:D}',now()-interval '1 day');
                    """);
            }
            await run(db,tx);
        }
        finally {await tx.RollbackAsync();}
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT oi04_line_probe");
        string? got=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException ex){got=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_line_probe");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_line_probe");
        }
        Assert.Equal(state,got);
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
