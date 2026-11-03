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
    private static readonly string Up = Assert.Single(
        new Phase2HMidMonthFinancialJournal().UpOperations
            .OfType<SqlOperation>()).Sql;
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
          "ContractId" uuid,"BuildingId" uuid,"SignedDeltaAmount" numeric(18,0)
        );
        CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions"(
          "Id" uuid PRIMARY KEY,"AdjustmentId" uuid,
          "Accepted" boolean,"ApprovedSignedDeltaAmount" numeric(18,0)
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
               "BankTransactionReference","RecordedByUserId","RecordedAtUtc")
            VALUES ('{Guid.NewGuid():D}','{f.Request:D}','{f.Contract:D}',
              '{f.Building:D}','{(kind==1?f.D03:f.D04):D}',
              {(kind==1?$"'{receivable??f.Receivable:D}'":"NULL")},
              {(kind==2?$"'{payment??f.Payment:D}'":"NULL")},
              {kind},{(amount??(kind==1?-50000m:1000000m)).ToString(System.Globalization.CultureInfo.InvariantCulture)},
              {(kind==1?"NULL":"'BANK-2H-ORIGINAL-RECEIPT-001'")},
              '{Guid.NewGuid():D}','2026-11-02T10:00:00Z')
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
                    ApprovedSignedDeltaVnd = -50000m,
                    Lines = new[] { new { Category = 1, SignedDeltaVnd = -50000m } }
                });
                var d04EvidenceJson = JsonSerializer.Serialize(new
                {
                    Leg = 1,
                    OriginalPaymentId = f.Payment,
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
                      '{f.Contract:D}','{f.Building:D}',-50000);
                    INSERT INTO "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
                     VALUES ('{f.Decision:D}','{f.Adjustment:D}',true,-50000);
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
