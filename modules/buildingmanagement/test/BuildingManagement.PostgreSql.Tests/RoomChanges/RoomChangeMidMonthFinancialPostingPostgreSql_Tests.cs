using System;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class RoomChangeMidMonthFinancialPostingPostgreSql_Tests
{
    private static readonly string Up = string.Join("\n",
        new Phase2HMidMonthFinancialJournal().UpOperations.OfType<SqlOperation>()
            .Concat(new Phase2HFinancialOriginalClaims().UpOperations.OfType<SqlOperation>())
            .Select(x => x.Sql));
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Stub = """
        CREATE TABLE "BuildingManagementContracts"("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeRequests"(
          "Id" uuid PRIMARY KEY,"SourceContractId" uuid,
          "SourceRoomId" uuid,"BuildingId" uuid,
          "Status" integer,"RequestedDate" date
        );
        CREATE TABLE "BuildingManagementRoomChangeEvidenceArchives"(
          "Id" uuid PRIMARY KEY,"RequestId" uuid,
          "SourceContractId" uuid,"BuildingId" uuid,
          "Kind" integer,"ArchivedAtUtc" timestamptz,
          "ArchivedByUserId" uuid,"EvidenceJson" text,
          "ParentEvidenceId" uuid,"ParentEvidenceSha256" varchar(64)
        );
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables"(
          "Id" uuid PRIMARY KEY,"Kind" integer,
          "IssuedInvoiceId" uuid,"ContractId" uuid,
          "RoomId" uuid,"BuildingId" uuid,
          "SourceEvidenceSha256" varchar(64),
          "OriginalAmount" numeric(18,0)
        );
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY,"CandidateSha256" varchar(64)
        );
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustments"(
          "Id" uuid PRIMARY KEY,"IssuedInvoiceId" uuid,
          "ContractId" uuid,"BuildingId" uuid,"SignedDeltaAmount" numeric(18,0),
          "ProposedByUserId" uuid,"ProposedAtUtc" timestamptz
        );
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions"(
          "Id" uuid PRIMARY KEY,"AdjustmentId" uuid,
          "Accepted" boolean,"ApprovedSignedDeltaAmount" numeric(18,0),
          "ReviewedByUserId" uuid,"ReviewedAtUtc" timestamptz
        );
        CREATE TABLE "BuildingManagementOfficialInvoiceReceiptReviews"(
          "Id" uuid PRIMARY KEY,"ReceivablePostingId" uuid,
          "Accepted" boolean,"Amount" numeric(18,0)
        );
        CREATE TABLE "BuildingManagementPayments"(
          "Id" uuid PRIMARY KEY,"Status" integer,"Purpose" integer,
          "Method" integer,"Channel" integer,
          "DepositDisposition" integer,"Amount" numeric(18,0),
          "ContractId" uuid,"TransactionReference" varchar(128),
          "IsDeleted" boolean,"InvoiceId" uuid,
          "RoomReservationId" uuid,"PaidAt" timestamptz,
          "Notes" text
        );
        """;

    private sealed record F(Guid Request,Guid Contract,Guid Room,
        Guid Building,Guid Actor,Guid Invoice,Guid Receivable,
        Guid Adjustment,Guid Decision,Guid D03,Guid D04,
        Guid Payment,Guid Target,Guid D01,Guid D06);

    private static string Posting(F f,int kind,decimal? amount=null,
        Guid? payment=null,Guid? receivable=null)
        => $"""
            INSERT INTO "BuildingManagementRoomChangeMidMonthFinancialPostings"
              ("Id","RequestId","SourceContractId","BuildingId","ArchiveId",
               "OfficialReceivableId","SourcePaymentId","Kind","SignedAmountVnd",
               "BankTransactionReference","RecordedByUserId","RecordedAtUtc","ApprovedAdjustmentId")
            VALUES ('{Guid.NewGuid():D}','{f.Request:D}','{f.Contract:D}',
              '{f.Building:D}','{(kind==1?f.D03:f.D04):D}',
              {(kind==1?$"'{receivable??f.Receivable:D}'":"NULL")},
              {(kind==2?$"'{payment??f.Payment:D}'":"NULL")},
              {kind},{(amount??(kind==1?-50000m:1000000m)).ToString(System.Globalization.CultureInfo.InvariantCulture)},
              {(kind==1?"NULL":"'BANK-2H-ORIGINAL-RECEIPT-001'")},
              '{Guid.NewGuid():D}','2026-11-02T10:00:00Z',
              {(kind==1?$"'{f.Adjustment:D}'":"NULL")})
            """;

    [Fact]
    public async Task TRFPOSTPG01_Reviewed_D03_Official_Receivable_Correction_Is_AppendOnly()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Posting(f,1));
            Assert.Equal(1L,await Count(db,tx));
            await Denied(db,tx,Posting(f,1),"23505");
            await Denied(db,tx,
                """UPDATE "BuildingManagementRoomChangeMidMonthFinancialPostings" SET "SignedAmountVnd"=1""","23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" ""","23514");
        });
    }

    [Fact]
    public async Task TRFPOSTPG02_Completed_D04_Target_Deposit_Receipt_Cannot_Replay()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Posting(f,2));
            await Denied(db,tx,Posting(f,2),"23505");
            await Denied(db,tx,Posting(f,2,payment:Guid.NewGuid()),"23514");
        });
    }

    [Fact]
    public async Task TRFPOSTPG03_Unverified_Credit_And_Outgoing_Refund_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Denied(db,tx,Posting(f,1,amount:-100000m),"23514");
            await Denied(db,tx,Posting(f,1,receivable:Guid.NewGuid()),"23514");
            await Denied(db,tx,Posting(f,3,amount:-1000000m),"23514");
            await Run(db,tx,$"""
                DELETE FROM "BuildingManagementRoomChangeEvidenceArchives"
                WHERE "Id"='{f.D06:D}'
                """);
            await Denied(db,tx,Posting(f,1),"23514");
        });
    }

    [Fact]
    public async Task TRFPOSTPG04_Lower_Isolation_And_Truncate_Denied()
    {
        await Scratch(IsolationLevel.ReadCommitted,async(db,tx,f)=>{
            await Denied(db,tx,Posting(f,1),"23514");
            await Denied(db,tx,Posting(f,2),"23514");
        });
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementRoomChangeMidMonthFinancialPostings" ""","23514");
        });
    }


    [Fact]
    public async Task TRFPOSTPG05_Rearchiving_The_Same_Approved_D03_Does_Not_Double_Post()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Run(db,tx,Posting(f,1));
            var anotherArchive = Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementRoomChangeEvidenceArchives"
                SELECT '{anotherArchive:D}',"RequestId","SourceContractId","BuildingId",
                    "Kind","ArchivedAtUtc","ArchivedByUserId","EvidenceJson",
                    "ParentEvidenceId","ParentEvidenceSha256"
                FROM "BuildingManagementRoomChangeEvidenceArchives"
                WHERE "Id"='{f.D03:D}'
                """);
            var duplicate = Posting(f,1).Replace(
                f.D03.ToString("D"), anotherArchive.ToString("D"),
                StringComparison.Ordinal);
            await Denied(db,tx,duplicate,"23505");
            Assert.Equal(1L,await Count(db,tx));
        });
    }

    [Fact]
    public async Task TRFPOSTPG06_D03_Proposer_And_Reviewer_Cannot_Be_One_Person()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Run(db,tx,$"""
                UPDATE "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
                SET "ReviewedByUserId" = (
                    SELECT "ProposedByUserId"
                    FROM "BuildingManagementMonthlyInvoiceAdjustments"
                    WHERE "Id"='{f.Adjustment:D}'
                )
                WHERE "Id"='{f.Decision:D}'
                """);
            await Denied(db,tx,Posting(f,1),"23514");
        });
    }

    [Fact]
    public async Task TRFPOSTPG07_D04_Missing_Independent_Bank_Evidence_Denied()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Run(db,tx,$"""
                UPDATE "BuildingManagementRoomChangeEvidenceArchives"
                SET "EvidenceJson" = (
                  ("EvidenceJson"::jsonb
                   - 'BankStatementReference'
                   || jsonb_build_object('BankStatementReference',''))::text
                )
                WHERE "Id"='{f.D04:D}'
                """);
            await Denied(db,tx,Posting(f,2),"23514");
        });
    }

    [Fact]
    public async Task TRFPOSTPG08_Both_Original_D03_D04_Postings_Coexist_Without_Money_Movement()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Run(db,tx,Posting(f,1));
            await Run(db,tx,Posting(f,2));
            Assert.Equal(2L,await Count(db,tx));
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
                WHERE ("Kind"=1 AND "ApprovedAdjustmentId" IS NOT NULL)
                   OR ("Kind"=2 AND "ApprovedAdjustmentId" IS NULL)
                """,db,tx);
            Assert.Equal(2L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            await Denied(db,tx,Posting(f,3,amount:-1000000m),"23514");
        });
    }

    private static async Task Scratch(IsolationLevel isolation,
        Func<NpgsqlConnection,NpgsqlTransaction,F,Task> action)
    {
        var safe=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(safe)
        {Pooling=false,CommandTimeout=45}.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_2h_midfin_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stub);
                await Run(db,tx,Up);
                var f=new F(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
                // Serialize JSON separately: braces in an interpolated C# raw
                // SQL string otherwise conflict with C# interpolation delimiters.
                var emptyEvidenceJson = "{}";
                var d03EvidenceJson = JsonSerializer.Serialize(new
                {
                    IssuedInvoiceId = f.Invoice,
                    AdjustmentId = f.Adjustment,
                    DecisionId = f.Decision,
                    ClassifiedByUserId = f.Actor,
                    ApprovedSignedDeltaVnd = -50000m,
                    Lines = new[] { new { Category = 1, SignedDeltaVnd = -50000m } }
                });
                var d04EvidenceJson = JsonSerializer.Serialize(new
                {
                    Leg = 1,
                    OriginalPaymentId = f.Payment,
                    ApprovedByOwnerId = Guid.NewGuid(),
                    IndependentBankReviewerId = f.Actor,
                    PaidAtUtc = new DateTime(2026,10,16,9,0,0,DateTimeKind.Utc),
                    ReviewedAtUtc = new DateTime(2026,10,17,10,0,0,DateTimeKind.Utc),
                    OwnerApprovedDestinationReference = "OWNER-APPROVED-TARGET-PAYMENT",
                    BankStatementReference = "BANK-STATEMENT-ORIGINAL-2026",
                    SignedBankReviewReference = "INDEPENDENT-REVIEW-SIGN-2026",
                    ExactAmountVnd = 1000000m,
                    TransactionReference = "BANK-2H-ORIGINAL-RECEIPT-001",
                    TargetContractId = f.Target
                });
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementContracts" VALUES ('{f.Contract:D}');
                    INSERT INTO "BuildingManagementRoomChangeRequests" VALUES
                     ('{f.Request:D}','{f.Contract:D}','{f.Room:D}',
                      '{f.Building:D}',0,'2026-10-15');
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger"
                     VALUES ('{f.Invoice:D}','{Hash}');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceivables"
                     VALUES ('{f.Receivable:D}',1,'{f.Invoice:D}',
                      '{f.Contract:D}','{f.Room:D}','{f.Building:D}','{Hash}',3000000);
                    INSERT INTO "BuildingManagementMonthlyInvoiceAdjustments"
                     VALUES ('{f.Adjustment:D}','{f.Invoice:D}',
                      '{f.Contract:D}','{f.Building:D}',-50000,
                        '{Guid.NewGuid():D}','2026-10-20T10:00:00Z');
                    INSERT INTO "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
                     VALUES ('{f.Decision:D}','{f.Adjustment:D}',true,-50000,
                       '{Guid.NewGuid():D}','2026-10-21T10:00:00Z');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceiptReviews"
                     VALUES ('{Guid.NewGuid():D}','{f.Receivable:D}',true,200000);
                    INSERT INTO "BuildingManagementPayments" VALUES
                     ('{f.Payment:D}',2,2,2,1,1,1000000,'{f.Target:D}',
                       'BANK-2H-ORIGINAL-RECEIPT-001',false,NULL,NULL,
                       '2026-10-16T09:00:00Z','BANK-PROOF: SIGNED-INDEPENDENT-STMT-2026');
                    INSERT INTO "BuildingManagementRoomChangeEvidenceArchives" VALUES
                     ('{f.D01:D}','{f.Request:D}','{f.Contract:D}','{f.Building:D}',
                      1,'2026-11-01T09:00:00Z','{f.Actor:D}','{emptyEvidenceJson}',
                      '{Guid.NewGuid():D}','{Hash}'),
                     ('{f.D06:D}','{f.Request:D}','{f.Contract:D}','{f.Building:D}',
                      4,'2026-11-01T09:00:00Z','{f.Actor:D}','{emptyEvidenceJson}',
                      '{Guid.NewGuid():D}','{Hash}'),
                     ('{f.D03:D}','{f.Request:D}','{f.Contract:D}','{f.Building:D}',
                      2,'2026-11-01T09:00:00Z','{f.Actor:D}',
                      '{d03EvidenceJson}',
                      '{f.Invoice:D}','{Hash}'),
                     ('{f.D04:D}','{f.Request:D}','{f.Contract:D}','{f.Building:D}',
                      3,'2026-11-01T09:00:00Z','{f.Actor:D}',
                      '{d04EvidenceJson}',
                      '{Guid.NewGuid():D}','{Hash}');
                    """);
                await action(db,tx,f);
            }
            finally {await tx.RollbackAsync();}
        }
        await using var cmd=new NpgsqlCommand(
            "SELECT count(*) FROM pg_namespace WHERE nspname=@n",db);
        cmd.Parameters.AddWithValue("n",schema);
        Assert.Equal(0L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    private static async Task<long> Count(NpgsqlConnection db,NpgsqlTransaction tx)
    {
        await using var cmd=new NpgsqlCommand(
          """SELECT count(*) FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" """,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,
        string sql,string expected)
    {
        await Run(db,tx,"SAVEPOINT financial_probe");
        string? actual=null;
        try{await Run(db,tx,sql);}
        catch(PostgresException ex){actual=ex.SqlState;}
        finally{
            await Run(db,tx,"ROLLBACK TO SAVEPOINT financial_probe");
            await Run(db,tx,"RELEASE SAVEPOINT financial_probe");
        }
        Assert.Equal(expected,actual);
    }
    private static async Task Run(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
