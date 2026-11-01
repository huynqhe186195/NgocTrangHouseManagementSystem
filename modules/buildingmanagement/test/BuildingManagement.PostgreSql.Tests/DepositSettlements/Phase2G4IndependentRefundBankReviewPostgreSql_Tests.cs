using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class Phase2G4IndependentRefundBankReviewPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2G4IndependentRefundBankReview().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2G4IndependentRefundBankReview().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string Tables = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementMoveOutRequests" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementContractDepositSettlementDecisions"(
            "Id" uuid PRIMARY KEY, "ContractId" uuid, "MoveOutRequestId" uuid,
            "BuildingId" uuid, "ApprovedByUserId" uuid,
            "ApprovedDebtOffsetAmount" numeric(18,0),
            "ApprovedRefundAmount" numeric(18,0), "ApprovedAtUtc" timestamptz);
        CREATE TABLE "BuildingManagementContractDepositDebtOffsetPostings"(
            "Id" uuid PRIMARY KEY,"ContractId" uuid,
            "DebitHeldDepositVnd" numeric(18,0));
        CREATE TABLE "BuildingManagementDepositSettlements"(
            "Id" uuid PRIMARY KEY,"Notes" text);
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals"(
            "Id" uuid PRIMARY KEY,"ContractId" uuid,"MoveOutRequestId" uuid,
            "ApprovedDebtOffsetAmount" numeric(18,0),
            "ApprovedRefundAmount" numeric(18,0),
            "ApprovedByUserId" uuid,"RefundTransactionReference" text,
            "RefundPaidAtUtc" timestamptz,"RefundSettlementId" uuid);
        """;

    [Fact]
    public async Task OI04REFSQL01_Requires_Signed_Independent_Refund_At_Closeout()
    {
        await Scratch(async (db,tx,contract,request,owner,_,decision) =>
        {
            var settled=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements" VALUES
                 ('{settled:D}','MANUAL BANK REF BANK-REFUND-TRACE-0001 / BANK-STATEMENT-0001');
                """);
            await Denied(db,tx,Closeout(contract,request,owner,settled),
                "23514");
            var bankReview=Guid.NewGuid();
            await Run(db,tx,Review(bankReview,contract,request,decision,
                Guid.NewGuid(),2_350_000m));
            await Run(db,tx,Closeout(contract,request,owner,settled));
            Assert.Equal(1L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundBankReviews" """));
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementContractDepositRefundBankReviews"
                 SET "VerifiedRefundAmountVnd"=2700000 WHERE "Id"='{bankReview:D}';
                ""","23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL02_Duplicate_Bank_Reference_Or_Unsigned_Amt_Denied()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_700_000m),"23514");
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,Guid.Empty,2_350_000m),"23514");
            await Run(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m));
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m),"23505");
        });
    }

    [Fact]
    public async Task OI04REFSQL03_Downgrade_Only_Before_Signed_History()
    {
        await Scratch(async (db,tx,_,_,_,_,_) =>
        {
            await Run(db,tx,Down);
            await Run(db,tx,Up);
        });
    }

    private static string Review(Guid id,Guid contract,Guid request,
        Guid decision,Guid reviewer,decimal amount) => $"""
        INSERT INTO "BuildingManagementContractDepositRefundBankReviews" (
            "Id","ContractId","MoveOutRequestId","DepositDecisionId",
            "BuildingId","ReviewedByUserId","ReviewedAtUtc","PaidAtUtc",
            "VerifiedRefundAmountVnd","BankTransactionReference",
            "BankStatementEvidenceReference","SignedReviewReference",
            "ReviewJson","ReviewSha256")
        VALUES ('{id:D}','{contract:D}','{request:D}','{decision:D}',
          '{Building:D}','{reviewer:D}','2026-11-01T12:00:00Z',
          '2026-11-01T11:00:00Z',{amount},
          'BANK-REFUND-TRACE-0001','BANK-STATEMENT-0001',
          'SIGNED-INDEPENDENT-REVIEW-0001','{{}}','{new string('a',64)}')
        """;

    private static string Closeout(Guid contract,Guid request,Guid owner,
        Guid settlement) => $"""
        INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
          ("Id","ContractId","MoveOutRequestId","ApprovedDebtOffsetAmount",
           "ApprovedRefundAmount","ApprovedByUserId",
           "RefundTransactionReference","RefundPaidAtUtc","RefundSettlementId")
        VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
          400000,2350000,'{owner:D}','BANK-REFUND-TRACE-0001',
          '2026-11-01T11:00:00Z','{settlement:D}')
        """;

    private static readonly Guid Building=Guid.NewGuid();
    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Guid,Guid,Guid,Task> test)
    {
        var cs=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            {Pooling=false,CommandTimeout=45}.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema="bm_oi04_bank_"+Guid.NewGuid().ToString("N");
        var contract=Guid.NewGuid(); var request=Guid.NewGuid();
        var owner=Guid.NewGuid(); var reviewer=Guid.NewGuid();
        var decision=Guid.NewGuid();
        try
        {
            await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
            await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db,tx,Tables);
            await Run(db,tx,Up);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementMoveOutRequests" VALUES ('{request:D}');
                INSERT INTO "BuildingManagementContractDepositSettlementDecisions"
                  VALUES ('{decision:D}','{contract:D}','{request:D}',
                    '{Building:D}','{owner:D}',400000,2350000,
                    '2026-11-01T10:00:00Z');
                INSERT INTO "BuildingManagementContractDepositDebtOffsetPostings"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',400000);
                """);
            await test(db,tx,contract,request,owner,reviewer,decision);
        }
        finally {await tx.RollbackAsync();}
        Assert.Equal(0L,await Count(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }
    private static async Task Denied(NpgsqlConnection db,
        NpgsqlTransaction tx,string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT oi04_refund");
        string? error=null;
        try { await Run(db,tx,sql); }
        catch(PostgresException ex) {error=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_refund");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_refund");
        }
        Assert.Equal(state,error);
    }
    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<long> Count(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
