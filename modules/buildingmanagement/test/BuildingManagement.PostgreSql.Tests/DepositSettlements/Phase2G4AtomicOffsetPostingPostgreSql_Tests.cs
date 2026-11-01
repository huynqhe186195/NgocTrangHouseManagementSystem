using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// OI-04 immutable double-entry posting on a rollback-only random schema.
/// All money is artificial and all SQL changes roll back before returning.
/// </summary>
public sealed class Phase2G4AtomicOffsetPostingPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2G4AtomicDepositDebtOffsetPosting().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2G4AtomicDepositDebtOffsetPosting().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string CashSql = Assert.Single(
        new Phase2G4RequireCashBackedDebtApproval().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string FreezeSql = Assert.Single(
        new Phase2G4FreezeOffsetSources().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string NetCollectionSql = Assert.Single(
        new Phase2G4PostOffsetVerifiedCollections().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly Guid Contract = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();
    private static readonly Guid Room = Guid.NewGuid();
    private static readonly Guid Building = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly Guid Posting = Guid.NewGuid();
    private static readonly Guid Issued = Guid.NewGuid();
    private static readonly Guid Candidate = Guid.NewGuid();
    private static readonly Guid Receipt = Guid.NewGuid();
    private static readonly string Hash = new('a', 64);

    private static readonly string Tables = """
        CREATE TABLE "BuildingManagementContracts" (
          "Id" uuid PRIMARY KEY,"RoomId" uuid,"Status" integer,
          "DepositAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementMoveOutRequests" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,"RoomId" uuid,"Status" integer);
        CREATE TABLE "BuildingManagementPayments" (
          "Id" uuid PRIMARY KEY,"Amount" numeric(18,0),"ContractId" uuid,
          "RoomReservationId" uuid,"Purpose" integer,"Status" integer,
          "IsDeleted" boolean,"PaidAt" timestamptz,"DepositDisposition" integer);
        CREATE TABLE "BuildingManagementDepositSettlements" (
          "Id" uuid PRIMARY KEY,"Type" integer,"Status" integer,
          "Amount" numeric(18,0),"PaymentId" uuid,
          "SourceRoomReservationId" uuid,"SourceContractId" uuid,
          "TargetContractId" uuid);
        CREATE TABLE "BuildingManagementContractDepositSettlementDecisions" ("ContractId" uuid);
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" ("ContractId" uuid);
        CREATE TABLE "BuildingManagementIssuedInvoiceLedger" ("Id" uuid,"ContractId" uuid);
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,"CandidateId" uuid);
        CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,
          "FrozenEvidenceSha256" varchar(64),
          "RentAmount" numeric(18,0),"ElectricityAmount" numeric(18,0),
          "WaterAmount" numeric(18,0),"AuxiliaryAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustments" ("IssuedInvoiceId" uuid);
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions" ("IssuedInvoiceId" uuid);
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables" (
          "Id" uuid PRIMARY KEY,"Kind" integer,"IssuedInvoiceId" uuid,
          "ContractId" uuid,"SourceEvidenceSha256" varchar(64));
        CREATE TABLE "BuildingManagementOfficialInvoiceReceiptReviews" (
          "ReceivablePostingId" uuid,"ReviewedAtUtc" timestamptz);
        CREATE TABLE "BuildingManagementOfficialMonthlyReceiptLineAllocations" (
          "ReceivablePostingId" uuid,"Category" integer,"Amount" numeric(18,0));
        CREATE TABLE "BuildingManagementContractDepositDebtReviews" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,"VerifiedHeldAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementContractDepositDebtOffsetApprovals" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,"MoveOutRequestId" uuid,
          "BuildingId" uuid,"ApprovedByOwnerId" uuid,"ApprovedAtUtc" timestamptz,
          "VerifiedHeldAmount" numeric(18,0),"ApprovedOffsetAmount" numeric(18,0),
          "ExpectedRemainingDeposit" numeric(18,0),"ExpectedUncoveredDebt" numeric(18,0),
          "ApprovalJson" text,"ApprovalSha256" varchar(64));
        """;

    private static ContractDepositDebtOffsetApprovalRecord Approval() =>
        ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), Contract, Request, Building, Owner, Reviewer,
            DateTime.UtcNow.AddMinutes(-4), 3_000_000m,
            "SIGNED-OWNER-OI04-DEBT-POST-POLICY-001",
            "SIGNED-INDEPENDENT-DEBT-REVIEW-001", Claims());
    private static ContractDepositOffsetClaim[] Claims() =>
        new[] { new ContractDepositOffsetClaim(
            ContractDepositOfficialMonthlyClaimBridge.ClaimIdFor(
                Posting, ContractDepositOffsetCategory.UnpaidRent),
            ContractDepositOffsetCategory.UnpaidRent,
            1_000_000m, true, false, Hash) };
    private static ContractDepositDebtOffsetPostingRecord Execution(
        ContractDepositDebtOffsetApprovalRecord approval) =>
        ContractDepositDebtOffsetPostingRecord.Post(
            Guid.NewGuid(), Owner, DateTime.UtcNow.AddMinutes(-1),
            approval, 3_000_000m, Claims());

    [Fact]
    public async Task OI04POSTSQL01_Real_Source_Dual_Legs_Are_Persisted_And_Frozen()
    {
        await Scratch(async (db,tx) =>
        {
            var a = Approval(); var p = Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,InsertSql(p));
            Assert.Equal(1L, await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetPostings" """));
            await Denied(db,tx,InsertSql(Execution(a)),"23505");
            await Denied(db,tx,
                """UPDATE "BuildingManagementContractDepositDebtOffsetPostings" SET "CreditOfficialDebtVnd"=1""","23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementContractDepositDebtOffsetPostings" ""","23514");
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementContractDepositDebtOffsetPostings" ""","23514");
            await Denied(db,tx,DownSql,"23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL02_No_Forged_Cash_Debit_Or_Credit()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Denied(db,tx,InsertSql(p,900_000m),"23514");
            await Denied(db,tx,InsertSql(p,1_100_000m),"23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL03_Changed_Debt_And_Receipt_After_Signoff_Deny()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,
                """UPDATE "BuildingManagementMonthlyInvoiceCandidates" SET "RentAmount"=900000""");
            await Denied(db,tx,InsertSql(p),"23514");
            await Run(db,tx,
                """UPDATE "BuildingManagementMonthlyInvoiceCandidates" SET "RentAmount"=1000000""");
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews"
                  VALUES ('{Posting:D}',now())
                """);
            await Denied(db,tx,InsertSql(p),"23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL04_Duplicate_Source_And_Disposed_Receipt_Deny()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}',1,2,100000,'{Receipt:D}',NULL,NULL,NULL)
                """);
            await Denied(db,tx,InsertSql(p),"23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL06_Original_Money_And_Invoice_Sources_Frozen_After_Post()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,InsertSql(p));
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments"
                   SET "Amount"=1 WHERE "Id"='{Receipt:D}'
                ""","23514");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementMonthlyInvoiceCandidates"
                   SET "RentAmount"=1 WHERE "Id"='{Candidate:D}'
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositSettlementDecisions"
                  VALUES ('{Contract:D}')
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
                  VALUES ('{Contract:D}')
                ""","23514");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementDepositSettlements"
                   SET "Amount"=1 WHERE "TargetContractId"='{Contract:D}'
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews"
                  VALUES ('{Posting:D}',now())
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                  VALUES ('{Posting:D}',1,1000)
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyInvoiceAdjustments"
                  VALUES ('{Issued:D}')
                ""","23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL07_New_Payout_From_Same_Deposit_Is_Blocked()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,InsertSql(p));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}',1,2,100000,NULL,NULL,
                          '{Contract:D}',NULL)
                ""","23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL05_Downgrade_Only_When_No_Posted_Money_History()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,DownSql);
            Assert.Equal(0L,await Scalar(db,tx,
                """SELECT count(*) FROM pg_tables WHERE schemaname=current_schema() AND tablename='BuildingManagementContractDepositDebtOffsetPostings' """));
            await Run(db,tx,UpSql);
        });
    }

    [Fact]
    public async Task OI04POSTSQL09_Duplicate_Archived_Hash_Category_Cannot_Double_Match_Claim()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval();var p=Execution(a);
            await SeedApproval(db,tx,a);
            var candidate2=Guid.NewGuid();
            var issue2=Guid.NewGuid();
            var posting2=Guid.NewGuid();
            // An imported ledger can carry a repeated frozen SHA. A
            // signature for invoice 1 must never authorize invoice 2.
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                    ('{candidate2:D}','{Contract:D}','{Hash}',1000000,0,0,0);
                INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                    ('{issue2:D}','{Contract:D}','{candidate2:D}');
                INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                    ('{posting2:D}',1,'{issue2:D}','{Contract:D}','{Hash}');
                """);
            await Denied(db,tx,InsertSql(p),"23514");
            Assert.Equal(0L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetPostings" """));
        });
    }

    [Fact]
    public async Task OI04POSTSQL08_Two_Concurrent_Posts_Cannot_Both_Commit()
    {
        var cs=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var schema="bm_oi04_postrace_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            {Pooling=false,CommandTimeout=45}.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,admin.Database);
        var approval=Approval();
        try
        {
            await using (var setup=await admin.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                await Run(admin,setup,$"""CREATE SCHEMA "{schema}" """);
                await Run(admin,setup,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(admin,setup,Tables);
                await Run(admin,setup,$"""
                    INSERT INTO "BuildingManagementContracts" VALUES
                      ('{Contract:D}','{Room:D}',2,3000000);
                    INSERT INTO "BuildingManagementMoveOutRequests" VALUES
                      ('{Request:D}','{Contract:D}','{Room:D}',1);
                    INSERT INTO "BuildingManagementPayments" VALUES
                      ('{Receipt:D}',3000000,'{Contract:D}',NULL,2,2,FALSE,now(),1);
                    INSERT INTO "BuildingManagementDepositSettlements" VALUES
                      ('{Guid.NewGuid():D}',3,2,3000000,'{Receipt:D}',NULL,NULL,'{Contract:D}');
                    INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                      ('{Candidate:D}','{Contract:D}','{Hash}',1000000,0,0,0);
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                      ('{Issued:D}','{Contract:D}','{Candidate:D}');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                      ('{Posting:D}',1,'{Issued:D}','{Contract:D}','{Hash}');
                    """);
                await Run(admin,setup,CashSql);
                await Run(admin,setup,UpSql);
                await Run(admin,setup,FreezeSql);
                await SeedApproval(admin,setup,approval);
                await setup.CommitAsync();
            }
            await using var db1=new NpgsqlConnection(cs);
            await using var db2=new NpgsqlConnection(cs);
            await db1.OpenAsync();
            await db2.OpenAsync();
            await using var tx1=await db1.BeginTransactionAsync(IsolationLevel.Serializable);
            await using var tx2=await db2.BeginTransactionAsync(IsolationLevel.Serializable);
            await Run(db1,tx1,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db2,tx2,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db1,tx1,InsertSql(Execution(approval)));
            var competitor=Run(db2,tx2,InsertSql(Execution(approval)));
            await Task.Delay(150);
            Assert.False(competitor.IsCompleted);
            await tx1.CommitAsync();
            PostgresException? rejected=null;
            try { await competitor; }
            catch(PostgresException ex){rejected=ex;}
            Assert.NotNull(rejected);
            Assert.Contains(rejected!.SqlState,new[]{"23505","40001"});
            await tx2.RollbackAsync();
            await using var verify=new NpgsqlConnection(cs);
            await verify.OpenAsync();
            await using var verifyTx=await verify.BeginTransactionAsync();
            await Run(verify,verifyTx,$"""SET LOCAL search_path TO "{schema}" """);
            Assert.Equal(1L,await Scalar(verify,verifyTx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetPostings" """));
            Assert.Equal(1_000_000L,await Scalar(verify,verifyTx,
                """SELECT sum("CreditOfficialDebtVnd") FROM "BuildingManagementContractDepositDebtOffsetPostings" """));
            await verifyTx.RollbackAsync();
        }
        finally
        {
            // This committed random schema exists ONLY within this scratch
            // test and is destroyed even after a failed concurrent attempt.
            await Run(admin,null,$"""DROP SCHEMA IF EXISTS "{schema}" CASCADE""");
        }
    }

    [Fact]
    public async Task OI04POSTSQL10_Net_Bank_Collection_Accepted_No_Overpay_Or_Deposit_Reuse()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval(); var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,InsertSql(p));
            await SetupPostOffsetCollectionColumns(db,tx);
            var cash=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementPayments"
                 ("Id","Amount","ContractId","RoomReservationId",
                  "Purpose","Status","IsDeleted","PaidAt","DepositDisposition",
                  "InvoiceId","Method","Channel","TransactionReference","Notes")
                VALUES ('{cash:D}',83000,'{Contract:D}',NULL,
                    3,2,FALSE,now(),NULL,'{Issued:D}',2,1,
                    'OI04-NET-RECEIPT-UNIQUE-001','BANK-PROOF: INDEPENDENT-STATEMENT-001')
                """);
            Assert.Equal(1L,await Scalar(db,tx,$"""
                SELECT count(*) FROM "BuildingManagementPayments" WHERE "Id"='{cash:D}'
                """));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementPayments"
                 ("Id","Amount","ContractId","RoomReservationId",
                  "Purpose","Status","IsDeleted","PaidAt","DepositDisposition",
                  "InvoiceId","Method","Channel","TransactionReference","Notes")
                VALUES ('{Guid.NewGuid():D}',1,'{Contract:D}',NULL,
                    3,2,FALSE,now(),NULL,'{Issued:D}',2,1,
                    'OI04-EXCESS-RECEIPT-001','BANK-PROOF: BANK-STATEMENT-001')
                ""","23514");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementPayments" SET "Amount"=100000
                WHERE "Id"='{cash:D}'
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementPayments"
                 ("Id","Amount","ContractId","RoomReservationId",
                  "Purpose","Status","IsDeleted","PaidAt","DepositDisposition",
                  "InvoiceId","Method","Channel","TransactionReference","Notes")
                VALUES ('{Guid.NewGuid():D}',1,'{Contract:D}',NULL,
                    2,2,FALSE,now(),NULL,'{Issued:D}',2,1,
                    'OI04-FORGED-DEPOSIT-001','BANK-PROOF: BANK-STATEMENT-001')
                ""","23514");
        });
    }

    [Fact]
    public async Task OI04POSTSQL11_Reviewed_Receipt_And_Original_Fee_Are_Net_Of_Offset()
    {
        await Scratch(async (db,tx) =>
        {
            var a=Approval(); var p=Execution(a);
            await SeedApproval(db,tx,a);
            await Run(db,tx,InsertSql(p));
            await SetupPostOffsetCollectionColumns(db,tx);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews"
                  ("ReceivablePostingId","ReviewedAtUtc","Accepted","Amount","ContractId")
                VALUES ('{Posting:D}',now(),TRUE,83000,'{Contract:D}')
                """);
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews"
                  ("ReceivablePostingId","ReviewedAtUtc","Accepted","Amount","ContractId")
                VALUES ('{Posting:D}',now(),TRUE,1,'{Contract:D}')
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                   ("ReceivablePostingId","Category","Amount")
                VALUES ('{Posting:D}',1,1)
                ""","23514");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementOfficialInvoiceReceiptReviews"
                   SET "Amount"=1
                ""","23514");
        });
    }

    private static async Task SetupPostOffsetCollectionColumns(
        NpgsqlConnection db,NpgsqlTransaction tx)
    {
        // Existing older scratch tables model exactly the earlier migrations.
        // Extend only this random schema to the newer verified Payment
        // and independent review columns needed by the new guard.
        await Run(db,tx,"""
            ALTER TABLE "BuildingManagementPayments"
              ADD COLUMN "InvoiceId" uuid,
              ADD COLUMN "Method" integer,
              ADD COLUMN "Channel" integer,
              ADD COLUMN "TransactionReference" text,
              ADD COLUMN "Notes" text;
            ALTER TABLE "BuildingManagementOfficialInvoiceReceivables"
              ADD COLUMN "OriginalAmount" numeric(18,0) DEFAULT 1083000;
            ALTER TABLE "BuildingManagementOfficialInvoiceReceiptReviews"
              ADD COLUMN "ContractId" uuid,
              ADD COLUMN "Accepted" boolean,
              ADD COLUMN "Amount" numeric(18,0);
            ALTER TABLE "BuildingManagementIssuedInvoiceLedger"
              ADD COLUMN "MoveOutRequestId" uuid;
            """);
        await Run(db,tx,NetCollectionSql);
    }

    private static async Task SeedApproval(NpgsqlConnection db,
        NpgsqlTransaction tx,ContractDepositDebtOffsetApprovalRecord a)
    {
        var proof=a.ApprovalJson.Replace("'","''");
        await Run(db,tx,$"""
            INSERT INTO "BuildingManagementContractDepositDebtOffsetApprovals" VALUES
            ('{a.Id:D}','{Contract:D}','{Request:D}','{Building:D}',
             '{Owner:D}','{a.ApprovedAtUtc:yyyy-MM-ddTHH:mm:ss.ffffffZ}',
             3000000,1000000,2000000,0,
             '{proof}','{a.ApprovalSha256}');
            """);
    }

    private static string InsertSql(ContractDepositDebtOffsetPostingRecord p,
        decimal? creditOverride=null)
    {
        var proof=p.PostingJson.Replace("'","''");
        return $"""
            INSERT INTO "BuildingManagementContractDepositDebtOffsetPostings"
              ("Id","ContractId","MoveOutRequestId","BuildingId","ApprovalId",
               "PostedByOwnerId","PostedAtUtc","VerifiedHeldAmount","DebitHeldDepositVnd",
               "CreditOfficialDebtVnd","RemainingHeldDepositVnd",
               "RemainingOfficialDebtVnd","ApprovalSha256",
               "PostingJson","PostingSha256")
            VALUES ('{p.Id:D}','{Contract:D}','{Request:D}','{Building:D}',
                    '{p.ApprovalId:D}','{Owner:D}',
                    '{p.PostedAtUtc:yyyy-MM-ddTHH:mm:ss.ffffffZ}',
                    {p.VerifiedHeldAmount},{p.DebitHeldDepositVnd},
                    {creditOverride??p.CreditOfficialDebtVnd},
                    {p.RemainingHeldDepositVnd},{p.RemainingOfficialDebtVnd},
                    '{p.ApprovalSha256}','{proof}','{p.PostingSha256}')
            """;
    }

    private static async Task Scratch(Func<NpgsqlConnection,NpgsqlTransaction,Task> scenario)
    {
        var cs=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            { Pooling=false, CommandTimeout=45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema="bm_oi04_post_"+Guid.NewGuid().ToString("N");
        try
        {
            await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
            await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db,tx,Tables);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES
                  ('{Contract:D}','{Room:D}',2,3000000);
                INSERT INTO "BuildingManagementMoveOutRequests" VALUES
                  ('{Request:D}','{Contract:D}','{Room:D}',1);
                INSERT INTO "BuildingManagementPayments" VALUES
                  ('{Receipt:D}',3000000,'{Contract:D}',NULL,2,2,FALSE,now(),1);
                INSERT INTO "BuildingManagementDepositSettlements" VALUES
                  ('{Guid.NewGuid():D}',3,2,3000000,'{Receipt:D}',NULL,NULL,'{Contract:D}');
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                  ('{Candidate:D}','{Contract:D}','{Hash}',1000000,0,0,0);
                INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                  ('{Issued:D}','{Contract:D}','{Candidate:D}');
                INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                  ('{Posting:D}',1,'{Issued:D}','{Contract:D}','{Hash}');
                """);
            await Run(db,tx,CashSql);
            await Run(db,tx,UpSql);
            await Run(db,tx,FreezeSql);
            await scenario(db,tx);
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,
        string sql,string expected)
    {
        await Run(db,tx,"SAVEPOINT oi04_post");
        string? actual=null;
        try{await Run(db,tx,sql);}
        catch(PostgresException ex){actual=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_post");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_post");
        }
        Assert.Equal(expected,actual);
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
