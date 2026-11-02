using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

public sealed class Phase2G4DebtDisputesPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2G4DocumentedDebtDisputes().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2G4DocumentedDebtDisputes().DownOperations.OfType<SqlOperation>()).Sql;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OI04DISPUTE01_Open_Dispute_Blocks_Offset_Until_Accepted_Resolution(
        bool accepted)
    {
        await Scratch(async (db,tx,contract,request,posting,building) =>
        {
            var dispute=Guid.NewGuid();
            await Run(db,tx,Open(dispute,contract,request,posting,building));
            await Denied(db,tx,Offset(contract),"23514");
            await Run(db,tx,Resolve(dispute,contract,accepted));
            if (accepted) await Run(db,tx,Offset(contract));
            else await Denied(db,tx,Offset(contract),"23514");
            await Denied(db,tx,$"""
                UPDATE "BuildingManagementContractDepositDebtDisputes"
                SET "Reason"='REWRITTEN' WHERE "Id"='{dispute:D}'
                ""","23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task OI04DISPUTE02_No_Signed_Dispute_Can_Downgrade()
    {
        await Scratch(async (db,tx,_,_,_,_) =>
        {
            await Run(db,tx,Down);
            await Run(db,tx,Up);
        });
    }

    [Fact]
    public async Task OI04DISPUTE03_Invalid_Or_Foreign_Receivable_Denied()
    {
        await Scratch(async (db,tx,contract,request,_,building) =>
        {
            await Denied(db,tx,
                Open(Guid.NewGuid(),contract,request,Guid.NewGuid(),building),
                "23514");
        });
    }

    private static string Open(Guid id,Guid contract,Guid request,
        Guid posting,Guid building) => $"""
        INSERT INTO "BuildingManagementContractDepositDebtDisputes"
          ("Id","ContractId","MoveOutRequestId","BuildingId",
           "OfficialPostingId","OpenedByUserId","OpenedAtUtc",
           "Reason","EvidenceReference")
        VALUES ('{id:D}','{contract:D}','{request:D}','{building:D}',
          '{posting:D}','{Guid.NewGuid():D}',clock_timestamp(),
          'Tenant disputes this official charge','SIGNED-DISPUTE-EVIDENCE-0001')
        """;

    private static string Resolve(Guid dispute,Guid contract,bool accepted) => $"""
        INSERT INTO "BuildingManagementContractDepositDebtDisputeResolutions"
          ("Id","DisputeId","ContractId","OwnerId","ResolvedAtUtc",
           "DebtAccepted","SignedOwnerResolutionReference")
        VALUES ('{Guid.NewGuid():D}','{dispute:D}','{contract:D}',
          '{Guid.NewGuid():D}',clock_timestamp(),
          {(accepted ? "TRUE" : "FALSE")},'OWNER-SIGNED-DISPUTE-RESOLUTION-0001')
        """;

    private static string Offset(Guid contract) => $"""
        INSERT INTO "BuildingManagementContractDepositDebtOffsetPostings"
          ("Id","ContractId") VALUES ('{Guid.NewGuid():D}','{contract:D}')
        """;

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Guid,Guid,Task> test)
    {
        var cs=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            {Pooling=false,CommandTimeout=45}.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema="bm_oi04_dispute_"+Guid.NewGuid().ToString("N");
        var contract=Guid.NewGuid();var request=Guid.NewGuid();
        var building=Guid.NewGuid();var posting=Guid.NewGuid();
        try
        {
            await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
            await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
            await Run(db,tx,"""
                CREATE TABLE "BuildingManagementContracts"("Id" uuid PRIMARY KEY);
                CREATE TABLE "BuildingManagementMoveOutRequests"("Id" uuid PRIMARY KEY);
                CREATE TABLE "BuildingManagementOfficialInvoiceReceivables"(
                    "Id" uuid PRIMARY KEY,"ContractId" uuid,"BuildingId" uuid);
                CREATE TABLE "BuildingManagementContractDepositDebtOffsetPostings"(
                    "Id" uuid PRIMARY KEY,"ContractId" uuid);
                """);
            await Run(db,tx,Up);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}');
                INSERT INTO "BuildingManagementMoveOutRequests" VALUES ('{request:D}');
                INSERT INTO "BuildingManagementOfficialInvoiceReceivables"
                    VALUES ('{posting:D}','{contract:D}','{building:D}');
                """);
            await test(db,tx,contract,request,posting,building);
        }
        finally {await tx.RollbackAsync();}
        await using var check=new NpgsqlCommand(
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'",db);
        Assert.Equal(0L,Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,
        string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT oi04_dispute");
        string? got=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException ex){got=ex.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT oi04_dispute");
            await Run(db,tx,"RELEASE SAVEPOINT oi04_dispute");
        }
        Assert.Equal(state,got);
    }
    private static async Task Run(NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }
}
