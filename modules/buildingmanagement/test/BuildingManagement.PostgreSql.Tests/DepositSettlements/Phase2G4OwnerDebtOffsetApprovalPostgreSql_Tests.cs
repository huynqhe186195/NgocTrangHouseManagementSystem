using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class Phase2G4OwnerDebtOffsetApprovalPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2G4OwnerDebtOffsetApproval().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2G4OwnerDebtOffsetApproval().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string IndependentUpSql = Assert.Single(
        new Phase2G4IndependentDebtReview().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string IndependentDownSql = Assert.Single(
        new Phase2G4IndependentDebtReview().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly Guid ContractId = Guid.NewGuid();
    private static readonly Guid RequestId = Guid.NewGuid();
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid IssuedId = Guid.NewGuid();
    private static readonly Guid CandidateId = Guid.NewGuid();
    private static readonly Guid PostingId = Guid.NewGuid();
    private static readonly Guid BuildingId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();
    private static readonly string Hash = new('a', 64);

    private static readonly string FixtureTables = """
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
            "Status" integer NOT NULL, "DepositAmount" numeric(18,0) NOT NULL);
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "RoomId" uuid NOT NULL, "Status" integer NOT NULL);
        CREATE TABLE "BuildingManagementContractDepositSettlementDecisions" ("ContractId" uuid);
        CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" ("ContractId" uuid);
        CREATE TABLE "BuildingManagementDepositSettlements" (
            "SourceContractId" uuid, "Type" integer);
        CREATE TABLE "BuildingManagementIssuedInvoiceLedger" ("ContractId" uuid);
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid, "CandidateId" uuid);
        CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid,
            "FrozenEvidenceSha256" varchar(64),
            "RentAmount" numeric(18,0),
            "ElectricityAmount" numeric(18,0),
            "WaterAmount" numeric(18,0),
            "AuxiliaryAmount" numeric(18,0));
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustments" ("IssuedInvoiceId" uuid);
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions" ("IssuedInvoiceId" uuid);
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables" (
            "Id" uuid PRIMARY KEY,"ContractId" uuid,
            "Kind" integer,"IssuedInvoiceId" uuid,
            "SourceEvidenceSha256" varchar(64));
        CREATE TABLE "BuildingManagementOfficialMonthlyReceiptLineAllocations" (
            "ReceivablePostingId" uuid,"Category" integer,"Amount" numeric(18,0));
        """;

    private static ContractDepositDebtOffsetApprovalRecord Valid() =>
        ContractDepositDebtOffsetApprovalRecord.Approve(
            Guid.NewGuid(), ContractId, RequestId, BuildingId,
            OwnerId, ReviewerId, DateTime.UtcNow.AddMinutes(-2),
            3_000_000m, "SIGNED-OI04-OWNER-PRIORITY-REVIEW",
            "SIGNED-INDEPENDENT-DEBT-REVIEW-001",
            new [] {
                new ContractDepositOffsetClaim(Guid.NewGuid(),
                    ContractDepositOffsetCategory.UnpaidRent,
                    1_000_000m, true, false, Hash)
            });

    [Fact]
    public async Task OI04AUTHSQL01_Exact_Official_Debt_Persists_And_Is_Immutable()
    {
        await Scratch(async (db, tx) =>
        {
            var approved = Valid();
            await Insert(db, tx, approved);
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetApprovals" """));
            await Rejected(db, tx,
                """UPDATE "BuildingManagementContractDepositDebtOffsetApprovals" SET "ApprovedOffsetAmount"=1""",
                "23514");
            await Rejected(db, tx,
                """DELETE FROM "BuildingManagementContractDepositDebtOffsetApprovals" """,
                "23514");
            await Rejected(db, tx, DownSql, "23514");
            await Rejected(db, tx,
                """TRUNCATE "BuildingManagementContractDepositDebtOffsetApprovals" """,
                "23514");
        });
    }

    [Fact]
    public async Task OI04AUTHSQL02_Forged_Amount_And_Unapproved_Claim_Denied()
    {
        await Scratch(async (db, tx) =>
        {
            var valid = Valid();
            await Rejected(db, tx, InsertSql(valid, amountOverride: 800_000m),
                "23514");
            // A signature against unapproved source fees is not a real
            // review, even if the JSON and index amounts balance.
            var invented = ContractDepositDebtOffsetApprovalRecord.Approve(
                Guid.NewGuid(), ContractId, RequestId, BuildingId,
                OwnerId, ReviewerId, DateTime.UtcNow.AddMinutes(-2),
                3_000_000m, "SIGNED-OI04-OWNER-PRIORITY-REVIEW",
                "SIGNED-INDEPENDENT-DEBT-REVIEW-001",
                new [] {
                    new ContractDepositOffsetClaim(Guid.NewGuid(),
                        ContractDepositOffsetCategory.Electricity,
                        100_000m, true, false, Hash)
                });
            await Rejected(db, tx, InsertSql(invented), "23514");
        });
    }

    [Fact]
    public async Task OI04AUTHSQL03_NonSerial_Insert_Or_Double_Approval_Denied()
    {
        await Scratch(async (db, tx) =>
        {
            await Insert(db, tx, Valid());
            await Rejected(db, tx, InsertSql(Valid()), "23505");
        });
        await Scratch(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            await Run(db, tx, UpSql);
        });
    }

    [Fact]
    public async Task OI04AUTHSQL04_Signed_Decision_Downgrade_Empty_Is_Allowed()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,DownSql);
            Assert.Equal(0L, await Scalar(db,tx,
                """SELECT count(*) FROM pg_tables WHERE schemaname=current_schema() AND tablename='BuildingManagementContractDepositDebtOffsetApprovals' """));
            await Run(db,tx,UpSql);
        });
    }


    [Fact]
    public async Task OI04AUTHSQL05_Owner_Cannot_Approve_Before_Independent_Review()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,IndependentUpSql);
            var approved = Valid();
            await Rejected(db,tx,InsertSql(approved),"23514");
            await InsertIndependentReview(db,tx,approved);
            await Insert(db,tx,approved);
            Assert.Equal(1L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtReviews" """));
            Assert.Equal(1L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetApprovals" """));
            await Rejected(db,tx,IndependentDownSql,"23514");
            await Rejected(db,tx,
                """UPDATE "BuildingManagementContractDepositDebtReviews" SET "VerifiedOutstandingAmount"=2""",
                "23514");
            await Rejected(db,tx,
                """DELETE FROM "BuildingManagementContractDepositDebtReviews" """,
                "23514");
            await Rejected(db,tx,
                """TRUNCATE "BuildingManagementContractDepositDebtReviews" """,
                "23514");
        });
    }

    [Fact]
    public async Task OI04AUTHSQL06_Mismatch_Reviewed_Claims_Or_Cash_Denies_Owner()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,IndependentUpSql);
            var signed = Valid();
            await InsertIndependentReview(db,tx,signed);
            await Run(db,tx,
                """UPDATE "BuildingManagementMonthlyInvoiceCandidates" SET "RentAmount"=1500000""");
            await Rejected(db,tx,InsertSql(signed),"23514");
            await Run(db,tx,
                """UPDATE "BuildingManagementMonthlyInvoiceCandidates" SET "RentAmount"=1000000""");
            var different = Valid();
            await Rejected(db,tx,InsertSql(different),"23514");
        });
    }

    [Fact]
    public async Task OI04AUTHSQL07_Duplicate_Independent_Review_And_Proof_Forgery_Fail()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,IndependentUpSql);
            var first = Valid();
            await InsertIndependentReview(db,tx,first);
            await Rejected(db,tx,IndependentReviewInsertSql(first),"23505");
            var signed = Valid();
            await Rejected(db,tx,InsertSql(signed),"23514");
        });
    }

    [Fact]
    public async Task OI04AUTHSQL08_Empty_Independent_Review_Downgrade_Is_Reversible()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,IndependentUpSql);
            await Run(db,tx,IndependentDownSql);
            await Run(db,tx,IndependentUpSql);
        });
    }

    private static async Task InsertIndependentReview(NpgsqlConnection db,
        NpgsqlTransaction tx,ContractDepositDebtOffsetApprovalRecord approval)
        => await Run(db,tx,IndependentReviewInsertSql(approval));

    private static string IndependentReviewInsertSql(
        ContractDepositDebtOffsetApprovalRecord approval)
    {
        using var doc = JsonDocument.Parse(approval.ApprovalJson);
        var claims = JsonSerializer.Deserialize<ContractDepositOffsetClaim[]>(
            doc.RootElement.GetProperty("Claims").GetRawText())!;
        var review = ContractDepositDebtReviewRecord.Capture(
            Guid.NewGuid(), ContractId, RequestId, BuildingId, ReviewerId,
            DateOnly.FromDateTime(approval.ApprovedAtUtc),
            approval.ApprovedAtUtc.AddSeconds(-30), 3_000_000m, claims,
            approval.IndependentReviewReference);
        var json = review.ReviewJson.Replace("'","''");
        return $"""
            INSERT INTO "BuildingManagementContractDepositDebtReviews"(
              "Id","ContractId","MoveOutRequestId","BuildingId",
              "ReviewedByUserId","ReviewedAsOfDate","ReviewedAtUtc",
              "VerifiedHeldAmount","VerifiedOutstandingAmount",
              "SignedReviewReference","ReviewJson","ReviewSha256")
            VALUES ('{review.Id:D}','{ContractId:D}','{RequestId:D}',
              '{BuildingId:D}','{ReviewerId:D}',
              '{review.ReviewedAsOfDate:yyyy-MM-dd}',
              '{review.ReviewedAtUtc:yyyy-MM-ddTHH:mm:ss.ffffffZ}',
              {review.VerifiedHeldAmount},{review.VerifiedOutstandingAmount},
              '{review.SignedReviewReference}','{json}','{review.ReviewSha256}');
            """;
    }

    private static string InsertSql(
        ContractDepositDebtOffsetApprovalRecord approval,
        decimal? amountOverride = null)
    {
        var proof = approval.ApprovalJson.Replace("'", "''");
        return $"""
            INSERT INTO "BuildingManagementContractDepositDebtOffsetApprovals"
            ("Id","ContractId","MoveOutRequestId","BuildingId",
             "ApprovedByOwnerId","IndependentDebtReviewerId",
             "ApprovedAtUtc","VerifiedHeldAmount","ApprovedOffsetAmount",
             "ExpectedRemainingDeposit","ExpectedUncoveredDebt",
             "SignedPolicyReference","IndependentReviewReference",
             "ApprovalJson","ApprovalSha256")
            VALUES ('{approval.Id:D}','{ContractId:D}','{RequestId:D}',
              '{BuildingId:D}','{OwnerId:D}','{ReviewerId:D}',
              '{approval.ApprovedAtUtc:yyyy-MM-ddTHH:mm:ss.ffffffZ}',
              {approval.VerifiedHeldAmount},
              {amountOverride ?? approval.ApprovedOffsetAmount},
              {approval.ExpectedRemainingDeposit},
              {approval.ExpectedUncoveredDebt},
              '{approval.SignedPolicyReference}',
              '{approval.IndependentReviewReference}',
              '{proof}','{approval.ApprovalSha256}');
            """;
    }

    private static Task Insert(
        NpgsqlConnection db, NpgsqlTransaction tx,
        ContractDepositDebtOffsetApprovalRecord approval) =>
        Run(db, tx, InsertSql(approval));

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> scenario,
        bool unused = false)
    {
        var cs = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
        { Pooling=false, CommandTimeout=45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        await using var tx = await db.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);
        var schema = "bm_oi04_auth_" + Guid.NewGuid().ToString("N");
        try
        {
            await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
            await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db,tx,FixtureTables);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES
                 ('{ContractId:D}','{RoomId:D}',2,3000000);
                INSERT INTO "BuildingManagementMoveOutRequests" VALUES
                 ('{RequestId:D}','{ContractId:D}','{RoomId:D}',1);
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates" VALUES
                 ('{CandidateId:D}','{ContractId:D}','{Hash}',1000000,0,0,0);
                INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                 ('{IssuedId:D}','{ContractId:D}','{CandidateId:D}');
                INSERT INTO "BuildingManagementOfficialInvoiceReceivables" VALUES
                 ('{PostingId:D}','{ContractId:D}',1,'{IssuedId:D}','{Hash}');
                """);
            await Run(db,tx,UpSql);
            await scenario(db,tx);
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(0L, await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Rejected(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,
        string sqlState)
    {
        await Run(db,tx,"SAVEPOINT oi04_auth");
        string? got=null;
        try { await Run(db,tx,sql); }
        catch (PostgresException ex) { got=ex.SqlState; }
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_auth");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_auth");
        }
        Assert.Equal(sqlState,got);
    }

    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd = new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd = new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
