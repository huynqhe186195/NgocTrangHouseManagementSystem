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
    private static readonly string IntegrityUp = Assert.Single(
        new Phase2G4RefundBankReviewPayloadIntegrity().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string IntegrityDown = Assert.Single(
        new Phase2G4RefundBankReviewPayloadIntegrity().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string AllRefundUp = Assert.Single(
        new Phase2G4RequireBankReviewForAllRefunds().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string AllRefundDown = Assert.Single(
        new Phase2G4RequireBankReviewForAllRefunds().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string WallClockUp = Assert.Single(
        new Phase2G4RefundReviewUseWallClock().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string WallClockDown = Assert.Single(
        new Phase2G4RefundReviewUseWallClock().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string GlobalBankUp = Assert.Single(
        new Phase2G4GlobalBankEvidenceReferenceClaims().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string GlobalBankDown = Assert.Single(
        new Phase2G4GlobalBankEvidenceReferenceClaims().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string OpsUp = Assert.Single(
        new Phase2G4RefundInstructionsAndFailedTransfers().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string OpsDown = Assert.Single(
        new Phase2G4RefundInstructionsAndFailedTransfers().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string HolidayUp = Assert.Single(
        new Phase2G4RefundHolidaySnapshotAndSlaAnchor().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string HolidayDown = Assert.Single(
        new Phase2G4RefundHolidaySnapshotAndSlaAnchor().DownOperations
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
        CREATE TABLE "BuildingManagementPayments" (
            "Id" uuid PRIMARY KEY, "TransactionReference" varchar(128));
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
            await Denied(db,tx,OpsDown,"23514");
            await Denied(db,tx,GlobalBankDown,"23514");
            await Denied(db,tx,WallClockDown,"23514");
            await Denied(db,tx,AllRefundDown,"23514");
            await Denied(db,tx,IntegrityDown,"23514");
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
                decision,reviewer,2_350_000m),"23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL03_Downgrade_Only_Before_Signed_History()
    {
        await Scratch(async (db,tx,_,_,_,_,_) =>
        {
            await Run(db,tx,OpsDown);
            await Run(db,tx,GlobalBankDown);
            await Run(db,tx,WallClockDown);
            await Run(db,tx,AllRefundDown);
            await Run(db,tx,IntegrityDown);
            await Run(db,tx,Down);
            await Run(db,tx,Up);
            await Run(db,tx,IntegrityUp);
            await Run(db,tx,AllRefundUp);
            await Run(db,tx,WallClockUp);
            await Run(db,tx,GlobalBankUp);
            await Run(db,tx,OpsUp);
            if (withHolidaySla) await Run(db,tx,HolidayUp);
        });
    }


    [Fact]
    public async Task OI04REFSQL04_Invalid_Sha256_And_Archived_Amount_Cannot_Be_Inserted()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m,forgedDigest:true),"23514");
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m,archivedAmount:2_700_000m),"23514");
            Assert.Equal(0L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundBankReviews" """));
            await Run(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m));
        });
    }

    [Fact]
    public async Task OI04REFSQL05_Statement_Reference_Is_Not_Independent_Review_Signature()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m,
                reuseStatementAsSignature:true),"23514");
        });
    }


    [Fact]
    public async Task OI04REFSQL06_NoOffset_FullRefund_Needs_Independent_Bank_Statement()
    {
        await Scratch(async (db,tx,contract,request,owner,reviewer,decision) =>
        {
            await Run(db,tx,$"""
                DELETE FROM "BuildingManagementContractDepositDebtOffsetPostings"
                 WHERE "ContractId"='{contract:D}';
                UPDATE "BuildingManagementContractDepositSettlementDecisions"
                   SET "ApprovedDebtOffsetAmount"=0,
                       "ApprovedRefundAmount"=3000000
                 WHERE "Id"='{decision:D}';
                """);
            var settlement=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements" VALUES
                 ('{settlement:D}','MANUAL BANK REF BANK-REFUND-TRACE-0001 / BANK-STATEMENT-0001');
                """);
            await Denied(db,tx,
                Closeout(contract,request,owner,settlement,0m,3000000m),"23514");
            await Run(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,3000000m));
            await Run(db,tx,
                Closeout(contract,request,owner,settlement,0m,3000000m));
            await Denied(db,tx,AllRefundDown,"23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL07_NoOffset_Review_Does_Not_Allow_Another_Amount()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Run(db,tx,$"""
                DELETE FROM "BuildingManagementContractDepositDebtOffsetPostings"
                 WHERE "ContractId"='{contract:D}';
                UPDATE "BuildingManagementContractDepositSettlementDecisions"
                   SET "ApprovedDebtOffsetAmount"=0,
                       "ApprovedRefundAmount"=3000000
                 WHERE "Id"='{decision:D}';
                """);
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2350000m),"23514");
            await Run(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,3000000m));
        });
    }


    [Fact]
    public async Task OI04REFSQL11_Review_Signed_After_Transaction_Start_Is_Not_Future_Dated()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            // The test transaction began before migrations and fixture
            // inserts. now() is older than this new, genuine review.
            await Run(db,tx,Instruction(contract,request,decision));
            await Run(db,tx,$"""
                WITH ts AS (
                  SELECT clock_timestamp() AS reviewed
                ), proof AS (
                  SELECT ts.reviewed, jsonb_build_object(
                    'ContractId','{contract:D}',
                    'MoveOutRequestId','{request:D}',
                    'DepositDecisionId','{decision:D}',
                    'BuildingId','{Building:D}',
                    'ReviewedByUserId','{reviewer:D}',
                    'ReviewedAtUtc',ts.reviewed,
                    'PaidAtUtc','2026-11-01T11:00:00Z',
                    'VerifiedRefundAmountVnd',2350000,
                    'BankTransactionReference','BANK-LIVE-REVIEW-0001',
                    'BankStatementEvidenceReference','BANK-LIVE-STATEMENT-0001',
                    'SignedReviewReference','SIGNED-LIVE-REVIEW-0001'
                  )::text AS payload FROM ts
                )
                INSERT INTO "BuildingManagementContractDepositRefundBankReviews" (
                    "Id","ContractId","MoveOutRequestId","DepositDecisionId",
                    "BuildingId","ReviewedByUserId","ReviewedAtUtc","PaidAtUtc",
                    "VerifiedRefundAmountVnd","BankTransactionReference",
                    "BankStatementEvidenceReference","SignedReviewReference",
                    "ReviewJson","ReviewSha256")
                SELECT '{Guid.NewGuid():D}','{contract:D}','{request:D}','{decision:D}',
                  '{Building:D}','{reviewer:D}',proof.reviewed,
                  '2026-11-01T11:00:00Z',2350000,'BANK-LIVE-REVIEW-0001',
                  'BANK-LIVE-STATEMENT-0001','SIGNED-LIVE-REVIEW-0001',
                  proof.payload,encode(sha256(convert_to(proof.payload,'UTF8')),'hex')
                FROM proof
                """);
            Assert.Equal(1L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundBankReviews" """));
        });
    }


    [Fact]
    public async Task OI04REFSQL12_One_Transfer_Cannot_Fund_Both_Refund_And_Receipt()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Run(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementPayments" ("Id","TransactionReference")
                VALUES ('{Guid.NewGuid():D}',' bank-refund-trace-0001 ')
                ""","23514");
            await Denied(db,tx,GlobalBankDown,"23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementBankEvidenceClaims" ""","23514");
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementBankEvidenceClaims" ""","23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL13_Preexisting_Receipt_Blocks_Refund_Review()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementPayments" ("Id","TransactionReference")
                VALUES ('{Guid.NewGuid():D}','Bank-Refund-Trace-0001')
                """);
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m),"23514");
            Assert.Equal(0L,await Count(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundBankReviews" """));
        });
    }

    [Fact]
    public async Task OI04REFSQL14_Cannot_Reassign_Claimed_Receipt_Reference()
    {
        await Scratch(async (db,tx,_,_,_,_,_) =>
        {
            var id=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementPayments" ("Id","TransactionReference")
                VALUES ('{id:D}','BANK-CLAIMED-RECEIPT-0001')
                """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments"
                  SET "TransactionReference"='DIFFERENT-PROOF-0001'
                WHERE "Id"='{id:D}'
                ""","23514");
        });
    }


    [Fact]
    public async Task OI04REFSQL15_Refund_Review_Requires_Owner_Destination()
    {
        await Scratch(async (db,tx,contract,request,_,reviewer,decision) =>
        {
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m,createInstruction:false),"23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL16_Failed_Bank_Reference_Never_Becomes_Paid()
    {
        await Scratch(async (db,tx,contract,request,owner,reviewer,decision) =>
        {
            await Run(db,tx,Instruction(contract,request,decision));
            var instructionId=await ScalarGuid(db,tx,$"""
                SELECT "Id" FROM "BuildingManagementContractDepositRefundInstructions"
                 WHERE "ContractId"='{contract:D}'
                """);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositRefundFailedTransfers"
                  ("Id","InstructionId","ContractId","OwnerId","Reference",
                   "FailedAtUtc","Reason","BankEvidenceReference")
                VALUES ('{Guid.NewGuid():D}','{instructionId:D}','{contract:D}',
                  '{owner:D}','BANK-REFUND-TRACE-0001','2026-11-02T05:00:00Z',
                  'Bank rejected the attempted transfer','FAILED-BANK-EVIDENCE-0001')
                """);
            await Denied(db,tx,Review(Guid.NewGuid(),contract,request,
                decision,reviewer,2_350_000m),"23514");
            await Denied(db,tx,OpsDown,"23514");
        });
    }

    [Fact]
    public async Task OI04REFSQL17_Signed_Holiday_Snapshot_Correct_Deadline_And_Immutable()
    {
        await Scratch(async (db, tx, contract, request, _, _, decision) =>
        {
            // Decision was signed on Sunday 2026-11-01 Vietnam time.
            // The seventh weekday is Tuesday Nov 10 without holidays;
            // the signed Nov 4 holiday moves it to Wednesday Nov 11.
            await Denied(db, tx, HolidayInstruction(contract, request, decision,
                "[\"2026-11-04\"]", "2026-11-10"), "23514");
            await Denied(db, tx, HolidayInstruction(contract, request, decision,
                "[]", "2026-11-11"), "23514");

            await Run(db, tx, HolidayInstruction(contract, request, decision,
                "[\"2026-11-04\"]", "2026-11-11"));
            Assert.Equal(1L, await Count(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundInstructions" """));
            await Denied(db, tx,
                """UPDATE "BuildingManagementContractDepositRefundInstructions"
                      SET "StatutoryHolidaysJson"='[]' """, "23514");
            await Denied(db, tx, HolidayDown, "23514");
        }, withHolidaySla: true);
    }

    [Fact]
    public async Task OI04REFSQL18_Duplicate_Or_Backdated_Holiday_Instruction_Denied()
    {
        await Scratch(async (db, tx, contract, request, _, _, decision) =>
        {
            await Denied(db, tx, HolidayInstruction(contract, request, decision,
                "[\"2026-11-04\",\"2026-11-04\"]", "2026-11-11"), "23514");
            await Denied(db, tx, HolidayInstruction(contract, request, decision,
                "[\"2026-12-25\"]", "2026-11-11"), "23514");
            await Denied(db, tx, HolidayInstruction(contract, request, decision,
                "[\"2026-11-04\"]", "2026-11-11",
                "2026-11-01T09:30:00Z"), "23514");
            Assert.Equal(0L, await Count(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositRefundInstructions" """));
        }, withHolidaySla: true);
    }

    private static string HolidayInstruction(Guid contract, Guid request,
        Guid decision, string holidays, string due,
        string approved = "2026-11-01T10:30:00Z") => $"""
        INSERT INTO "BuildingManagementContractDepositRefundInstructions"
          ("Id","ContractId","MoveOutRequestId","DecisionId","BuildingId",
           "OwnerId","ApprovedAtUtc","ApprovedRefundAmountVnd","PayeeName",
           "PayeeBank","PayeeAccount","SignedDestinationReference",
           "HolidayCalendarReference","DueOn","StatutoryHolidaysJson")
        SELECT '{Guid.NewGuid():D}',d."ContractId",d."MoveOutRequestId",
          d."Id",d."BuildingId",d."ApprovedByUserId",'{approved}',
          d."ApprovedRefundAmount",'Nguyen Van A','Test Bank','123456789012',
          'OWNER-APPROVED-BENEFICIARY-0001',
          'VIETNAM-SIGNED-HOLIDAY-CALENDAR','{due}','{holidays}'
        FROM "BuildingManagementContractDepositSettlementDecisions" d
        WHERE d."Id"='{decision:D}' AND d."ContractId"='{contract:D}'
          AND d."MoveOutRequestId"='{request:D}'
        """;

    private static string Instruction(Guid contract,Guid request,Guid decision) => $"""
        INSERT INTO "BuildingManagementContractDepositRefundInstructions"
          ("Id","ContractId","MoveOutRequestId","DecisionId","BuildingId",
           "OwnerId","ApprovedAtUtc","ApprovedRefundAmountVnd","PayeeName",
           "PayeeBank","PayeeAccount","SignedDestinationReference",
           "HolidayCalendarReference","DueOn")
        SELECT '{Guid.NewGuid():D}',d."ContractId",d."MoveOutRequestId",d."Id",
          d."BuildingId",d."ApprovedByUserId",'2026-11-01T10:30:00Z',
          d."ApprovedRefundAmount",'Nguyen Van A','Test Bank','123456789012',
          'OWNER-APPROVED-BENEFICIARY-0001','VIETNAM-SIGNED-HOLIDAY-CALENDAR',
          '2026-11-11'
        FROM "BuildingManagementContractDepositSettlementDecisions" d
        WHERE d."Id"='{decision:D}' AND d."ContractId"='{contract:D}'
          AND d."MoveOutRequestId"='{request:D}'
        ON CONFLICT ("ContractId") DO NOTHING;
        """;

    private static async Task<Guid> ScalarGuid(NpgsqlConnection db,
        NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql,db,tx);
        return (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static string Review(Guid id, Guid contract, Guid request,
        Guid decision, Guid reviewer, decimal amount,
        decimal? archivedAmount = null, bool forgedDigest = false,
        bool reuseStatementAsSignature = false,
        bool createInstruction = true)
    {
        var archived = archivedAmount ?? amount;
        var signed = reuseStatementAsSignature
            ? "BANK-STATEMENT-0001"
            : "SIGNED-INDEPENDENT-REVIEW-0001";
        var digest = forgedDigest
            ? $"'{new string('a', 64)}'"
            : "encode(sha256(convert_to(proof.payload, 'UTF8')), 'hex')";
        return (createInstruction
            ? Instruction(contract,request,decision) : string.Empty) + $"""
            INSERT INTO "BuildingManagementContractDepositRefundBankReviews" (
                "Id","ContractId","MoveOutRequestId","DepositDecisionId",
                "BuildingId","ReviewedByUserId","ReviewedAtUtc","PaidAtUtc",
                "VerifiedRefundAmountVnd","BankTransactionReference",
                "BankStatementEvidenceReference","SignedReviewReference",
                "ReviewJson","ReviewSha256")
            SELECT '{id:D}','{contract:D}','{request:D}','{decision:D}',
              '{Building:D}','{reviewer:D}','2026-11-01T12:00:00Z',
              '2026-11-01T11:00:00Z',{amount},
              'BANK-REFUND-TRACE-0001','BANK-STATEMENT-0001',
              '{signed}',proof.payload,{digest}
            FROM (SELECT jsonb_build_object(
              'ContractId','{contract:D}',
              'MoveOutRequestId','{request:D}',
              'DepositDecisionId','{decision:D}',
              'BuildingId','{Building:D}',
              'ReviewedByUserId','{reviewer:D}',
              'ReviewedAtUtc','2026-11-01T12:00:00Z',
              'PaidAtUtc','2026-11-01T11:00:00Z',
              'VerifiedRefundAmountVnd',{archived},
              'BankTransactionReference','BANK-REFUND-TRACE-0001',
              'BankStatementEvidenceReference','BANK-STATEMENT-0001',
              'SignedReviewReference','{signed}'
            )::text AS payload) proof
            """;
    }

    private static string Closeout(Guid contract,Guid request,Guid owner,
        Guid settlement, decimal offset = 400000m,
        decimal refund = 2350000m) => $"""
        INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
          ("Id","ContractId","MoveOutRequestId","ApprovedDebtOffsetAmount",
           "ApprovedRefundAmount","ApprovedByUserId",
           "RefundTransactionReference","RefundPaidAtUtc","RefundSettlementId")
        VALUES ('{Guid.NewGuid():D}','{contract:D}','{request:D}',
          {offset},{refund},'{owner:D}','BANK-REFUND-TRACE-0001',
          '2026-11-01T11:00:00Z','{settlement:D}')
        """;

    private static readonly Guid Building=Guid.NewGuid();
    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Guid,Guid,Guid,Task> test,
        bool withHolidaySla = false)
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
            await Run(db,tx,IntegrityUp);
            await Run(db,tx,AllRefundUp);
            await Run(db,tx,WallClockUp);
            await Run(db,tx,GlobalBankUp);
            await Run(db,tx,OpsUp);
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
