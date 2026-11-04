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
/// Isolated SERIALIZABLE attack tests for the separate mid-month atomic
/// completion ledger. Stub parents are NOT business approval: there is
/// intentionally no fake positive signed financial transfer in this suite.
/// Full real-schema migrations and authenticated transfer must pass separately.
/// </summary>
public sealed class RoomChangeMidMonthCompletionPostgreSql_Tests
{
    private static readonly string Up=Assert.Single(
        new Phase2HMidMonthAtomicCompletion().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down=Assert.Single(
        new Phase2HMidMonthAtomicCompletion().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string AppendFencesUp=Assert.Single(
        new Phase2HMidMonthPostCommitAppendFences().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string AppendFencesDown=Assert.Single(
        new Phase2HMidMonthPostCommitAppendFences().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string Sha=
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string Stub = """
        CREATE TABLE "BuildingManagementContracts" (
          "Id" uuid PRIMARY KEY,"Status" integer,"RoomId" uuid);
        CREATE TABLE "BuildingManagementRooms" ("Id" uuid PRIMARY KEY,
          "Status" integer);
        CREATE TABLE "BuildingManagementRoomChangeRequests" (
          "Id" uuid PRIMARY KEY,"SourceContractId" uuid,
          "SourceRoomId" uuid,"TargetRoomId" uuid,
          "BuildingId" uuid,"RequestedDate" date,"Status" integer,
          "ReviewedByUserId" uuid,"ReviewedAtUtc" timestamptz,
          "ReviewNotes" text);
        CREATE TABLE "BuildingManagementRoomChangeBusinessPolicies"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeBusinessPolicyReviews"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeTenantRosterSnapshots"(
          "Id" uuid PRIMARY KEY,"RosterCount" integer NOT NULL DEFAULT 0);
        CREATE TABLE "BuildingManagementRoomChangeHandoverEvidence"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeEvidenceArchives"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeDayPricingRuleCandidates"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementOfficialInvoiceReceivables"(
          "Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementDepositSettlements"(
          "Id" uuid PRIMARY KEY,"SourceContractId" uuid,
          "TargetContractId" uuid);
        CREATE TABLE "BuildingManagementRoomChangeCompletions"(
          "Id" uuid PRIMARY KEY,"RequestId" uuid,
          "SourceContractId" uuid,"TargetContractId" uuid);
        CREATE TABLE "BuildingManagementContractTenants" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates" (
          "Id" uuid PRIMARY KEY,"ContractId" uuid,
          "BillingYear" integer,"BillingMonth" integer,
          "RentAmount" numeric(18,0));
        CREATE FUNCTION "BM_VerifyRoomChangeSourceDispositionAtCommit"()
          RETURNS trigger LANGUAGE plpgsql AS $safe$
          BEGIN RETURN NULL; END; $safe$;
        """;

    private sealed record F(
        Guid Req,Guid Source,Guid Target,Guid SourceRoom,Guid TargetRoom,
        Guid Building,Guid Policy,Guid Review,Guid Roster,Guid Handover,
        Guid PriceArchive,Guid HandoverArchive,Guid Rule,
        Guid Invoice,Guid Receivable,Guid Carry,Guid Actor);

    private static string Completion(F f,Guid? source=null)
        => $"""
            INSERT INTO "BuildingManagementRoomChangeMidMonthCompletions"
             ("Id","RequestId","SourceContractId","TargetContractId",
              "SourceRoomId","TargetRoomId","BuildingId",
              "PolicyId","PolicyReviewId","RosterSnapshotId",
              "HandoverEvidenceId","PricingArchiveId","ThreePartyArchiveId",
              "PricingRuleId","SourceMonthlyInvoiceId","SourceReceivableId",
              "SourceSignedAdjustmentVnd","SourceAdjustedReceivableVnd",
              "SourceReviewedReceiptsVnd","TargetFirstMonthRentVnd",
              "CarryForwardSettlementId","CarriedDepositVnd",
              "SourceRoomDisposition","DispositionEvidenceReference",
              "CompletionReference","CompletedByUserId","CompletedAtUtc",
              "EvidenceSha256")
             VALUES ('{Guid.NewGuid():D}','{f.Req:D}',
              '{source??f.Source:D}','{f.Target:D}',
              '{f.SourceRoom:D}','{f.TargetRoom:D}','{f.Building:D}',
              '{f.Policy:D}','{f.Review:D}','{f.Roster:D}','{f.Handover:D}',
              '{f.PriceArchive:D}','{f.HandoverArchive:D}','{f.Rule:D}',
              '{f.Invoice:D}','{f.Receivable:D}',
              -50000,2950000,2950000,1500000,
              '{f.Carry:D}',3000000,1,
              'INSPECTED-SOURCE-DISPOSITION',
              'SIGNED-ATOMIC-MID-MONTH-COMPLETION',
              '{f.Actor:D}','2026-11-03T16:00:00Z','{Sha}')
            """;

    [Fact]
    public async Task TRFMIDPG01_Missing_Original_Source_Is_Foreign_Key_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Denied(db,tx,Completion(f,Guid.NewGuid()),"23503");
        });
    }

    [Fact]
    public async Task TRFMIDPG02_Completion_Requires_Serializable()
    {
        await Scratch(IsolationLevel.ReadCommitted,async(db,tx,f)=>{
            await Denied(db,tx,Completion(f),"23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG03_Once_Inserted_Cannot_Update_Delete_Or_Truncate()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,
              """UPDATE "BuildingManagementRoomChangeMidMonthCompletions"
                 SET "SourceSignedAdjustmentVnd"=0""","23514");
            await Denied(db,tx,
              """DELETE FROM "BuildingManagementRoomChangeMidMonthCompletions" """,
              "23514");
            await Denied(db,tx,
              """TRUNCATE "BuildingManagementRoomChangeMidMonthCompletions" """,
              "23514");
            await Denied(db,tx,Down,"23514");
            await Denied(db,tx,AppendFencesDown,"23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG04_Future_Target_FullMonth_Rent_Still_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates"
                 ("Id","ContractId","BillingYear","BillingMonth","RentAmount")
                VALUES ('{Guid.NewGuid():D}','{f.Target:D}',2026,10,3000000)
                ""","23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG05_Target_Frozen_Prorated_Rent_Is_Recognized()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates"
                 ("Id","ContractId","BillingYear","BillingMonth","RentAmount")
                VALUES ('{Guid.NewGuid():D}','{f.Target:D}',2026,10,1500000)
                """);
        });
    }

    [Fact]
    public async Task TRFMIDPG06_Cannot_Write_Old_And_New_Completion_For_One_Source()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementRoomChangeCompletions"
                VALUES ('{Guid.NewGuid():D}','{f.Req:D}',
                  '{f.Source:D}','{f.Target:D}')
                ""","23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG07_Empty_Migration_Rollback_Is_Safe()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,AppendFencesDown);
            await Run(db,tx,Down);
        });
    }

    [Fact]
    public async Task TRFMIDPG08_Contract_End_And_Terminal_Request_Still_Have_Gates()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await using var cmd=new NpgsqlCommand("""
              SELECT count(*) FROM pg_proc
               WHERE pronamespace=current_schema()::regnamespace
                 AND proname IN (
                'BM_RequireAtomicRoomChangeAtContractEnd',
                'BM_VerifyRoomChangeTerminalRequest',
                'BM_Verify2HMidMonthCommit',
                'BM_Require2HMidTargetFirstMonthProration')
              """,db,tx);
            Assert.Equal(4L,Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        });
    }

    [Fact]
    public async Task TRFMIDPG09_PostCompletion_Target_Tenant_Append_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{f.Target:D}',FALSE);
                SET CONSTRAINTS "TR_BM_2HMid_RosterAppend" IMMEDIATE;
                ""","23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG10_PostCompletion_Source_Tenant_Append_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{f.Source:D}',FALSE);
                SET CONSTRAINTS "TR_BM_2HMid_RosterAppend" IMMEDIATE;
                ""","23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG11_PostCompletion_Source_Cash_Append_Denied()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                    ("Id","SourceContractId","TargetContractId")
                VALUES ('{Guid.NewGuid():D}','{f.Source:D}',NULL);
                SET CONSTRAINTS "TR_BM_2HMid_CashAppend" IMMEDIATE;
                ""","23514");
            await Denied(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                    ("Id","SourceContractId","TargetContractId")
                VALUES ('{Guid.NewGuid():D}',NULL,'{f.Source:D}');
                SET CONSTRAINTS "TR_BM_2HMid_CashAppend" IMMEDIATE;
                ""","23514");
        });
    }

    [Fact]
    public async Task TRFMIDPG12_Original_Atomic_Tenant_Roster_Append_Remains_Permitted()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,$"""
                UPDATE "BuildingManagementRoomChangeTenantRosterSnapshots"
                   SET "RosterCount"=1 WHERE "Id"='{f.Roster:D}';
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{f.Source:D}',FALSE);
                """);
            await Run(db,tx,Completion(f));
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContractTenants"
                    ("Id","ContractId","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{f.Target:D}',FALSE);
                SET CONSTRAINTS "TR_BM_2HMid_RosterAppend" IMMEDIATE;
                """);
        });
    }

    [Fact]
    public async Task TRFMIDPG13_Original_Atomic_Carry_Append_Remains_Permitted()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            var fresh=f with {Carry=Guid.NewGuid()};
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementDepositSettlements"
                    ("Id","SourceContractId","TargetContractId")
                VALUES ('{fresh.Carry:D}','{f.Source:D}','{f.Target:D}');
                """);
            await Run(db,tx,Completion(fresh));
            await Run(db,tx,
                """SET CONSTRAINTS "TR_BM_2HMid_CashAppend" IMMEDIATE""");
        });
    }

    [Fact]
    public async Task TRFMIDPG14_Cannot_Edit_Frozen_FirstMonth_Rent_To_Full_Month()
    {
        await Scratch(IsolationLevel.Serializable,async(db,tx,f)=>{
            await Run(db,tx,Completion(f));
            var id=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyInvoiceCandidates"
                    ("Id","ContractId","BillingYear","BillingMonth","RentAmount")
                VALUES ('{id:D}','{f.Target:D}',2026,10,1500000);
                """);
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementMonthlyInvoiceCandidates"
                   SET "RentAmount"=3000000 WHERE "Id"='{id:D}';
                ""","23514");
        });
    }

    private static async Task Scratch(IsolationLevel isolation,
        Func<NpgsqlConnection,NpgsqlTransaction,F,Task> work)
    {
        var safe=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(safe)
            {Pooling=false,CommandTimeout=45}.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_2h_midcommit_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stub);
                await Run(db,tx,Up);
                await Run(db,tx,AppendFencesUp);
                var f=new F(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid());
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementContracts" VALUES
                     ('{f.Source:D}',2,'{f.SourceRoom:D}'),
                     ('{f.Target:D}',2,'{f.TargetRoom:D}');
                    INSERT INTO "BuildingManagementRooms" VALUES
                     ('{f.SourceRoom:D}',1),('{f.TargetRoom:D}',2);
                    INSERT INTO "BuildingManagementRoomChangeRequests" VALUES
                     ('{f.Req:D}','{f.Source:D}','{f.SourceRoom:D}',
                      '{f.TargetRoom:D}','{f.Building:D}','2026-10-15',4,
                      '{f.Actor:D}','2026-11-03T16:00:00Z',
                      'SIGNED-ATOMIC-MID-MONTH-COMPLETION');
                    INSERT INTO "BuildingManagementRoomChangeBusinessPolicies"
                      VALUES ('{f.Policy:D}');
                    INSERT INTO "BuildingManagementRoomChangeBusinessPolicyReviews"
                      VALUES ('{f.Review:D}');
                    INSERT INTO "BuildingManagementRoomChangeTenantRosterSnapshots"
                      VALUES ('{f.Roster:D}');
                    INSERT INTO "BuildingManagementRoomChangeHandoverEvidence"
                      VALUES ('{f.Handover:D}');
                    INSERT INTO "BuildingManagementRoomChangeEvidenceArchives"
                      VALUES ('{f.PriceArchive:D}'),('{f.HandoverArchive:D}');
                    INSERT INTO "BuildingManagementRoomChangeDayPricingRuleCandidates"
                      VALUES ('{f.Rule:D}');
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger"
                      VALUES ('{f.Invoice:D}');
                    INSERT INTO "BuildingManagementOfficialInvoiceReceivables"
                      VALUES ('{f.Receivable:D}');
                    INSERT INTO "BuildingManagementDepositSettlements"
                      VALUES ('{f.Carry:D}','{f.Source:D}','{f.Target:D}');
                    """);
                await work(db,tx,f);
            }
            finally{await tx.RollbackAsync();}
        }
        await using var verify=new NpgsqlCommand(
            "SELECT count(*) FROM pg_namespace WHERE nspname=@n",db);
        verify.Parameters.AddWithValue("n",schema);
        Assert.Equal(0L,Convert.ToInt64(await verify.ExecuteScalarAsync()));
    }

    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,
        string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT mid_commit_denied");
        string? actual=null;
        try{await Run(db,tx,sql);}
        catch(PostgresException ex){actual=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT mid_commit_denied");
            await Run(db,tx,"RELEASE SAVEPOINT mid_commit_denied");
        }
        Assert.Equal(state,actual);
    }

    private static async Task Run(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
