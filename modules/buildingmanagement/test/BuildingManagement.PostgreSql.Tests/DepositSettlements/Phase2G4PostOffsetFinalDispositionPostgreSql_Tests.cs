using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real PostgreSQL checks of the additive OI-04 closeout migration: exact
/// cash/credit conservation, immutable posted source, signed decision
/// amount, and downgrade refusal. An isolated schema rolls back every row.
/// Older official-invoice and receipt-reconciliation guards have their own
/// dedicated PostgreSQL suites and are not replaced by this narrow fixture.
/// </summary>
public sealed class Phase2G4PostOffsetFinalDispositionPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2G4PostOffsetFinalDisposition().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2G4PostOffsetFinalDisposition().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Tables = """
        CREATE TABLE "BuildingManagementContracts"(
          "Id" uuid PRIMARY KEY, "DepositAmount" numeric(18,0), "Status" integer);
        CREATE TABLE "BuildingManagementMoveOutRequests"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid, "RoomId" uuid,
          "Status" integer);
        CREATE TABLE "BuildingManagementIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid, "RoomId" uuid,
          "MoveOutRequestId" uuid, "BuildingId" uuid, "IssuedAtUtc" timestamptz,
          "TotalAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid);
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid, "Kind" integer,
          "IssuedInvoiceId" uuid,"OriginalAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementOfficialInvoiceReceiptReviews"(
          "Id" uuid PRIMARY KEY, "ReceivablePostingId" uuid,
          "Accepted" boolean, "Amount" numeric(18,0), "ReviewedAtUtc" timestamptz);
        CREATE TABLE "BuildingManagementMonthlyInvoicePaymentAllocation"(
          "Id" uuid PRIMARY KEY, "IssuedInvoiceId" uuid);
        CREATE TABLE "BuildingManagementInvoicePaymentAllocationJournal"(
          "Id" uuid PRIMARY KEY, "IssuedInvoiceId" uuid, "Amount" numeric(18,0),
          "ContractId" uuid, "PaymentId" uuid);
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions"(
          "IssuedInvoiceId" uuid, "Accepted" boolean,
          "ApprovedSignedDeltaAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementPayments"(
          "Id" uuid PRIMARY KEY, "InvoiceId" uuid,
          "ContractId" uuid, "Amount" numeric(18,0),
          "Purpose" integer, "Status" integer, "IsDeleted" boolean);
        CREATE TABLE "BuildingManagementContractDepositDebtOffsetPostings"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid,
          "MoveOutRequestId" uuid, "DebitHeldDepositVnd" numeric(18,0));
        CREATE TABLE "BuildingManagementDepositSettlements"(
          "Id" uuid PRIMARY KEY, "SourceContractId" uuid,
          "TargetContractId" uuid, "Type" integer, "Status" integer,
          "Amount" numeric(18,0), "CompletedAt" timestamptz, "Notes" text);
        CREATE TABLE "BuildingManagementContractDepositSettlementDecisions"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid,
          "MoveOutRequestId" uuid, "ApprovedAtUtc" timestamptz,
          "VerifiedHeldAmount" numeric(18,0),
          "ApprovedForfeitAmount" numeric(18,0),
          "ApprovedRefundAmount" numeric(18,0),
          "ApprovedDebtOffsetAmount" numeric(18,0),
          CONSTRAINT "CK_BM_DepositDecision_Amounts" CHECK (
            "VerifiedHeldAmount" > 0 AND "ApprovedForfeitAmount" >= 0
            AND "ApprovedRefundAmount" >= 0 AND
            "ApprovedDebtOffsetAmount" = 0 AND "VerifiedHeldAmount" =
            "ApprovedForfeitAmount" + "ApprovedRefundAmount"));
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals"(
          "Id" uuid PRIMARY KEY, "VerifiedHeldAmount" numeric(18,0),
          "ApprovedForfeitAmount" numeric(18,0),
          "ApprovedRefundAmount" numeric(18,0),
          CONSTRAINT "CK_BM_DepositCloseout_Amounts" CHECK (
            "VerifiedHeldAmount" > 0 AND "ApprovedForfeitAmount" >= 0
            AND "ApprovedRefundAmount" >= 0 AND "VerifiedHeldAmount" =
            "ApprovedForfeitAmount" + "ApprovedRefundAmount"));
        CREATE FUNCTION "BM_PostOffsetAppliedToInvoice"(uuid,uuid)
          RETURNS numeric LANGUAGE sql AS 'SELECT 0::numeric';
        CREATE FUNCTION "BM_BlockPostedOffsetSourceMutation"()
          RETURNS trigger LANGUAGE plpgsql AS $old$
        BEGIN
          IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
          RETURN NEW;
        END; $old$;
        CREATE TRIGGER "TR_BM_OI04FreezeLegacyDepositDecision"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositSettlementDecisions"
          FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
        CREATE TRIGGER "TR_BM_OI04FreezeLegacyCloseoutApproval"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositCloseoutApprovals"
          FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
        CREATE TRIGGER "TR_BM_OI04FreezeOffsetSettlements"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementDepositSettlements"
          FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
        """;

    [Fact]
    public async Task OI04FINALSQL01_Posted_Credit_And_Completed_Payout_Must_Balance()
    {
        await Scratch(async (db, tx, contract, request) =>
        {
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositDebtOffsetPostings"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',400000);
                INSERT INTO "BuildingManagementContractDepositSettlementDecisions"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
                    '2026-11-01T10:00:00Z',3000000,250000,2350000,400000);
                """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,2,
                    2750000,'2026-11-01T11:00:00Z','MANUAL BANK REF forged');
                """, "23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,1,
                    2350000,NULL,'MANUAL BANK REF pending');
                """, "23514");
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,2,
                    2350000,'2026-11-01T11:00:00Z',
                    'MANUAL BANK REF independently-reviewed-evidence');
                """);
            Assert.Equal(1L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementDepositSettlements" """));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,2,
                    2350000,'2026-11-01T11:05:00Z','MANUAL BANK REF replay');
                ""","23505");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementDepositSettlements"
                  SET "Amount"=2400000 WHERE "SourceContractId"='{contract:D}';
                ""","23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task OI04FINALSQL02_Offset_Mismatch_And_Replay_Cannot_Create_New_Refund()
    {
        await Scratch(async (db,tx,contract,request) =>
        {
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositDebtOffsetPostings"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',400000);
                INSERT INTO "BuildingManagementContractDepositSettlementDecisions"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
                    '2026-11-01T10:00:00Z',3000000,0,2700000,300000);
                """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,2,2700000,
                    '2026-11-01T11:00:00Z','MANUAL BANK REF wrong-credit');
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositSettlementDecisions"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
                    '2026-11-01T10:00:00Z',3000000,0,3000000,400000);
                ""","23514");
        });
    }

    [Fact]
    public async Task OI04FINALSQL03_No_Offset_Allows_Original_Unchanged_Financial_Lane()
    {
        await Scratch(async (db,tx,contract,_) =>
        {
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}',NULL,1,2,
                    3000000,'2026-11-01T11:00:00Z','LEGACY-CASE');
                """);
            Assert.Equal(1L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementDepositSettlements" """));
            await Run(db,tx,Down);
            await Run(db,tx,Up);
        });
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Task> scenario)
    {
        var cs=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            { Pooling=false,CommandTimeout=45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema="bm_oi04_close_"+Guid.NewGuid().ToString("N");
        var contract=Guid.NewGuid();
        var request=Guid.NewGuid();
        try
        {
            await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
            await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db,tx,Tables);
            await Run(db,tx,Up);
            // Narrow money-disposition fixture: previous 2G.4 official
            // receivable provenance and signed approval are covered by
            // their separate real-PG suites. Replace only the upstream
            // approval trigger here, so this test isolates NEW final
            // refund and anti-replay rules without fabricating invoices.
            await Run(db,tx,"""
                CREATE OR REPLACE FUNCTION "BM_GuardDepositSettlementDecision"()
                RETURNS trigger LANGUAGE plpgsql AS $test_scope$
                BEGIN RETURN NEW; END; $test_scope$;
                """);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts"
                  VALUES ('{contract:D}',3000000,2);
                """);
            await scenario(db,tx,contract,request);
        }
        finally {await tx.RollbackAsync();}
        Assert.Equal(0L,await Count(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT oi04_final");
        string? actual=null;
        try { await Run(db,tx,sql); }
        catch(PostgresException ex) {actual=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_final");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_final");
        }
        Assert.Equal(state,actual);
    }
    private static async Task Run(
        NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<long> Count(
        NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
