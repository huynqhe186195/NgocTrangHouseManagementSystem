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
/// Six real PostgreSQL positive/adversarial probes run ONLY against the
/// repository's allowlisted integration test database, in a disposable
/// transaction and random schema. No real contract, cash or bank is altered.
/// </summary>
public sealed class RoomChangeDepositDispositionIntentPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HDepositDispositionIntentEvidence().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HDepositDispositionIntentEvidence().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string Stubs = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeRequests" (
          "Id" uuid PRIMARY KEY, "SourceContractId" uuid,
          "BuildingId" uuid, "RequestedDate" date, "Status" integer);
        CREATE TABLE "BuildingManagementRoomChangeMidMonthCompletions" (
          "RequestId" uuid);
        """;
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed record Fixture(Guid Request, Guid Source, Guid Target,
        Guid Building);

    private static async Task<Fixture> Seed(
        NpgsqlConnection db,NpgsqlTransaction tx,int status=0)
    {
        var f=new Fixture(Guid.NewGuid(),Guid.NewGuid(),
            Guid.NewGuid(),Guid.NewGuid());
        await Run(db,tx,$"""
            INSERT INTO "BuildingManagementContracts"("Id")
              VALUES('{f.Source:D}');
            INSERT INTO "BuildingManagementRoomChangeRequests"
              ("Id","SourceContractId","BuildingId","RequestedDate","Status")
              VALUES('{f.Request:D}','{f.Source:D}','{f.Building:D}',
               '2026-11-15',{status});
            """);
        return f;
    }

    private static string Intent(Fixture f,
        decimal sourceHeld=3000000m,decimal deductions=0m,
        decimal carry=3000000m,decimal targetRequired=4000000m,
        decimal topup=1000000m,decimal credit=0m,decimal refund=0m,
        int choice=0,string? due="2026-12-15",
        Guid? source=null,Guid? building=null,Guid? request=null)
    {
        var date=due is null ? "NULL" : $"'{due}'";
        var deductedDoc=deductions>0m
            ? "'SIGNED-POSTED-SOURCE-DEDUCTION-REVIEW-2026'"
            : "NULL";
        return $"""
            INSERT INTO "BuildingManagementRoomChangeDepositDispositionIntents"
             ("Id","RequestId","SourceContractId","TargetContractId",
              "BuildingId","TransferDateVietnam",
              "ClaimedOriginalSourceHeldVnd",
              "AlreadyPostedApprovedDeductionsVnd",
              "ProposedCarryTargetVnd","TargetRequiredDepositVnd",
              "ProposedUnpaidTopUpVnd","ProposedTopUpDueDateVietnam",
              "ProposedFutureRentCreditVnd","ProposedManualRefundVnd",
              "SurplusChoice","OriginalCashLedgerReference",
              "TargetDepositPolicyReference","SignedDeductionReference",
              "OwnerSignedDispositionReference","ProposedByOwnerId",
              "ProposedAtUtc","EvidenceSha256")
            VALUES('{Guid.NewGuid():D}','{(request??f.Request):D}',
              '{(source??f.Source):D}','{f.Target:D}',
              '{(building??f.Building):D}','2026-11-15',
              {sourceHeld},{deductions},{carry},{targetRequired},{topup},
              {date},{credit},{refund},{choice},
              'CASH-LEDGER-REQUIRES-INDEPENDENT-VERIFICATION',
              'SIGNED-NEW-CONTRACT-DEPOSIT-POLICY-2026',
              {deductedDoc},
              'OWNER-SIGNED-EXCLUSIVE-DEPOSIT-INTENT-2026',
              '{Guid.NewGuid():D}','2026-11-04T08:00:00Z','{Hash}')
            """;
    }

    [Fact]
    public async Task TRFH4PG01_Deferred_TopUp_Intent_Is_Recorded_Not_Collected()
    {
        await Scratch(async(db,tx)=>
        {
            var f=await Seed(db,tx);
            await Run(db,tx,Intent(f));
            Assert.Equal(1000000L,await Scalar(db,tx,
                """
                SELECT "ProposedUnpaidTopUpVnd"
                FROM "BuildingManagementRoomChangeDepositDispositionIntents"
                """));
            Assert.Equal(1L,await Scalar(db,tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeDepositDispositionIntents"
                """));
        });
    }

    [Fact]
    public async Task TRFH4PG02_Credit_And_Refund_Are_Exclusive_Alternatives()
    {
        await Scratch(async(db,tx)=>
        {
            var credit=await Seed(db,tx);
            var refund=await Seed(db,tx);
            await Run(db,tx,Intent(credit,carry:2000000m,
                targetRequired:2000000m,topup:0m,
                credit:1000000m,choice:1,due:null));
            await Run(db,tx,Intent(refund,carry:2000000m,
                targetRequired:2000000m,topup:0m,
                refund:1000000m,choice:2,due:null));
            Assert.Equal(2L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeDepositDispositionIntents" """));
        });
    }

    [Fact]
    public async Task TRFH4PG03_Late_One_Month_Due_And_Double_Use_Denied()
    {
        await Scratch(async(db,tx)=>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Intent(f,due:"2026-12-16"),"23514");
            await Denied(db,tx,Intent(f,credit:1m),"23514");
            await Denied(db,tx,Intent(f,sourceHeld:3500000m),"23514");
            await Run(db,tx,Intent(f));
        });
    }

    [Fact]
    public async Task TRFH4PG04_Cross_Building_Or_Foreign_Source_Denied()
    {
        await Scratch(async(db,tx)=>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Intent(f,building:Guid.NewGuid()),"23514");
            // Existing but different source contract proves this is a source
            // ownership refusal, rather than a missing foreign key.
            var other=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts"("Id")
                  VALUES('{other:D}')
                """);
            await Denied(db,tx,Intent(f,source:other),"23514");
        });
    }

    [Fact]
    public async Task TRFH4PG05_Reject_Closed_Request_And_Replay_Or_Mutation()
    {
        await Scratch(async(db,tx)=>
        {
            var closed=await Seed(db,tx,status:2);
            await Denied(db,tx,Intent(closed),"23514");
            var f=await Seed(db,tx);
            await Run(db,tx,Intent(f));
            await Denied(db,tx,Intent(f),"23505");
            await Denied(db,tx,
                """
                UPDATE "BuildingManagementRoomChangeDepositDispositionIntents"
                   SET "ProposedUnpaidTopUpVnd"=0
                ""","23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementRoomChangeDepositDispositionIntents" """,
                "23514");
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementRoomChangeDepositDispositionIntents" """,
                "23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task TRFH4PG06_Empty_Downgrade_And_Reupgrade_Allowed()
    {
        await Scratch(async(db,tx)=>
        {
            await Run(db,tx,Down);
            Assert.Equal(0L,await Scalar(db,tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomChangeDepositDispositionIntents'
                   AND relnamespace=current_schema()::regnamespace
                """));
            await Run(db,tx,Up);
            var f=await Seed(db,tx);
            await Run(db,tx,Intent(f));
        });
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> test)
    {
        var cs=new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            {Pooling=false,CommandTimeout=30}.ConnectionString;
        await using var db=new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_h4_intent_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stubs);
                await Run(db,tx,Up);
                await test(db,tx);
            }
            finally {await tx.RollbackAsync();}
        }
        Assert.Equal(0L,await Scalar(db,null,$"""
            SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'
            """));
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,string code)
    {
        await Run(db,tx,"SAVEPOINT h4_probe");
        string? actual=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException e){actual=e.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT h4_probe");
            await Run(db,tx,"RELEASE SAVEPOINT h4_probe");
        }
        Assert.Equal(code,actual);
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
