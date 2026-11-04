using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Runs real additive D03 source-binding migration in disposable, randomly
/// named schema of the allowlisted local PostgreSQL TEST database only.
/// A transaction rollback removes every schema, row and trigger.
/// </summary>
public sealed class RoomServiceInvoiceSourceBindingsPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HRoomServiceInvoiceSourceBindings().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HRoomServiceInvoiceSourceBindings().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string TariffHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CandidateHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BoundHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SignedReference = "SIGNED-ROOM-301-SERVICE-PRICE-2026-11";

    private sealed record Fixture(
        Guid Candidate, Guid Room, Guid Building, Guid Contract,
        Guid Service, Guid Tariff);

    private const string Stubs = """
        CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates"(
          "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
          "RoomId" uuid NOT NULL, "BuildingId" uuid NOT NULL,
          "BillingYear" integer NOT NULL,"BillingMonth" integer NOT NULL,
          "FrozenEvidenceSha256" varchar(64) NOT NULL,
          "FrozenEvidenceJson" text NOT NULL,
          "AuxiliaryAmount" numeric NOT NULL,
          "ReviewedAtUtc" timestamptz NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomScopedServiceTariffVersions"(
          "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
          "ServiceId" uuid NOT NULL, "Version" integer NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          "UnitPriceVnd" numeric NOT NULL,
          "BillingUnit" integer NOT NULL,"ChargingRule" integer NOT NULL,
          "EffectiveFrom" date NOT NULL,"EffectiveThrough" date,
          "ApprovedAtUtc" timestamptz NOT NULL,
          "SignedPolicyReference" varchar(500) NOT NULL
        );
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "CandidateId" uuid NOT NULL
        );
        """;

    private static async Task<Fixture> Seed(
        NpgsqlConnection db, NpgsqlTransaction tx, int feeDecision=2)
    {
        var f = new Fixture(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var fee = new {
            ServiceCode="INTERNET",Amount=300000,
            ContractOrRateReference=SignedReference
        };
        var json = JsonSerializer.Serialize(new {
            AuxiliaryFeeDecision=feeDecision,
            AuxiliaryCharges=feeDecision==2 ? new[] {fee} : Array.Empty<object>()
        }).Replace("'", "''", StringComparison.Ordinal);
        await Run(db,tx,$"""
            INSERT INTO "BuildingManagementMonthlyInvoiceCandidates"
             ("Id","ContractId","RoomId","BuildingId","BillingYear",
              "BillingMonth","FrozenEvidenceSha256","FrozenEvidenceJson",
              "AuxiliaryAmount","ReviewedAtUtc")
            VALUES('{f.Candidate:D}','{f.Contract:D}','{f.Room:D}',
              '{f.Building:D}',2026,11,'{CandidateHash}',
              '{json}',{(feeDecision==2?300000:0)},
              '2026-11-04T08:00:00Z')
            """);
        await Run(db,tx,$"""
            INSERT INTO "BuildingManagementRoomScopedServiceTariffVersions"
             ("Id","RoomId","ServiceId","Version","EvidenceSha256",
              "UnitPriceVnd","BillingUnit","ChargingRule",
              "EffectiveFrom","EffectiveThrough","ApprovedAtUtc",
              "SignedPolicyReference")
            VALUES('{f.Tariff:D}','{f.Room:D}','{f.Service:D}',1,
              '{TariffHash}',150000,1,2,'2026-11-01',NULL,
              '2026-10-20T08:00:00Z','{SignedReference}')
            """);
        return f;
    }

    private static string Bind(
        Fixture f, Guid? room=null, Guid? tariff=null,
        decimal amount=300000m, int occupants=2,
        string code="INTERNET",
        string from="2026-11-01",string through="2026-11-30",
        string? candidateHash=null, decimal? meter=null)
    {
        var meterLiteral = meter is null ? "NULL" :
            meter.Value.ToString(CultureInfo.InvariantCulture);
        return $"""
            INSERT INTO "BuildingManagementRoomServiceInvoiceSourceBindings"
             ("MonthlyCandidateId","ContractId","RoomId","BuildingId",
              "BillingYear","BillingMonth","ServiceId","ServiceCode",
              "OriginalTariffVersionId","OriginalTariffVersion",
              "OriginalTariffSha256","OriginalFullPeriodUnitPriceVnd",
              "BillingUnit","ChargingRule","ServiceFromVietnam",
              "ServiceThroughVietnam","VerifiedOccupantCount",
              "VerifiedMeterQuantity","VerifiedChargeSourceReference",
              "FrozenAuxiliaryLineAmountVnd","OriginalMonthlyCandidateSha256",
              "EvidenceSha256")
            VALUES('{f.Candidate:D}','{f.Contract:D}','{(room??f.Room):D}',
              '{f.Building:D}',2026,11,'{f.Service:D}','{code}',
              '{(tariff??f.Tariff):D}',1,'{TariffHash}',150000,1,2,
              '{from}','{through}',{occupants},{meterLiteral},
              'VERIFIED-ORIGINAL-ROOM-ROSTER-SOURCE-2026',
              {amount.ToString(CultureInfo.InvariantCulture)},
              '{candidateHash??CandidateHash}','{BoundHash}')
            """;
    }

    [Fact]
    public async Task TRFBINDPG01_Original_Matching_Room_Fee_Binds_And_Is_Ready()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            Assert.False(await Ready(db,tx,f.Candidate));
            await Run(db,tx,Bind(f));
            Assert.True(await Ready(db,tx,f.Candidate));
            Assert.Equal(1L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementRoomServiceInvoiceSourceBindings" """));
        });
    }

    [Fact]
    public async Task TRFBINDPG02_Foreign_Room_Or_Unknown_Tariff_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Bind(f,room:Guid.NewGuid()),"23514");
            await Denied(db,tx,Bind(f,tariff:Guid.NewGuid()),"23514");
            Assert.False(await Ready(db,tx,f.Candidate));
        });
    }

    [Fact]
    public async Task TRFBINDPG03_Rewritten_Amount_And_Candidate_Hash_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Bind(f,amount:300001m),"23514");
            await Denied(db,tx,Bind(f,
                candidateHash=new string('d',64)),"23514");
            Assert.Equal(0L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementRoomServiceInvoiceSourceBindings" """));
        });
    }

    [Fact]
    public async Task TRFBINDPG04_Source_Date_And_Unverified_Occupants_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Bind(f,from:"2026-10-01"),"23514");
            await Denied(db,tx,Bind(f,occupants:1),"23514");
            await Denied(db,tx,Bind(f,meter:1m),"23514");
        });
    }

    [Fact]
    public async Task TRFBINDPG05_Unapproved_Service_Code_And_NoFee_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,Bind(f,code:"PARKING"),"23514");
        });
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx,feeDecision:1);
            Assert.True(await Ready(db,tx,f.Candidate));
            await Denied(db,tx,Bind(f),"23514");
        });
    }

    [Fact]
    public async Task TRFBINDPG06_Append_Only_Replay_And_Downgrade_Denied()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementRoomServiceInvoiceSourceBindings" """,
                "23514");
            await Run(db,tx,Bind(f));
            await Denied(db,tx,Bind(f),"23505");
            await Denied(db,tx,
                """
                UPDATE "BuildingManagementRoomServiceInvoiceSourceBindings"
                   SET "FrozenAuxiliaryLineAmountVnd"=1
                ""","23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementRoomServiceInvoiceSourceBindings" """,
                "23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task TRFBINDPG07_Later_Effective_Tariff_Invalidates_Old_WholeMonth()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementRoomScopedServiceTariffVersions"
                 ("Id","RoomId","ServiceId","Version","EvidenceSha256",
                  "UnitPriceVnd","BillingUnit","ChargingRule",
                  "EffectiveFrom","EffectiveThrough","ApprovedAtUtc",
                  "SignedPolicyReference")
                VALUES('{Guid.NewGuid():D}','{f.Room:D}','{f.Service:D}',2,
                  '{TariffHash}',200000,1,2,'2026-11-15',NULL,
                  '2026-10-20T08:00:00Z','{SignedReference}')
                """);
            await Denied(db,tx,Bind(f),"23514");
        });
    }

    [Fact]
    public async Task TRFBINDPG08_Cannot_Add_Source_After_Monthly_Issuance()
    {
        await Scratch(async (db,tx) =>
        {
            var f=await Seed(db,tx);
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger"
                 ("CandidateId") VALUES('{f.Candidate:D}')
                """);
            await Denied(db,tx,Bind(f),"23514");
        });
    }

    [Fact]
    public async Task TRFBINDPG09_Empty_Rollback_And_Reupgrade_Are_Allowed()
    {
        await Scratch(async (db,tx) =>
        {
            await Run(db,tx,Down);
            Assert.Equal(0L,await Scalar(db,tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomServiceInvoiceSourceBindings'
                   AND relnamespace=current_schema()::regnamespace
                """));
            await Run(db,tx,Up);
            Assert.Equal(1L,await Scalar(db,tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomServiceInvoiceSourceBindings'
                   AND relnamespace=current_schema()::regnamespace
                """));
        });
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Task> work)
    {
        var cs=new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        {Pooling=false,CommandTimeout=30}.ConnectionString;
        await using var db=new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_roomsvc_bind_"+Guid.NewGuid().ToString("N");
        await using (var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stubs);
                await Run(db,tx,Up);
                await work(db,tx);
            }
            finally {await tx.RollbackAsync();}
        }
        Assert.Equal(0L,await Scalar(db,null,$"""
            SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'
            """));
    }

    private static async Task<bool> Ready(
        NpgsqlConnection db,NpgsqlTransaction tx,Guid candidate)
    {
        await using var cmd=new NpgsqlCommand(
            """SELECT "BM_2HRoomServiceInvoiceBindingsReady"(@candidate)""",db,tx);
        cmd.Parameters.AddWithValue("candidate",candidate);
        return (bool)(await cmd.ExecuteScalarAsync()??false);
    }

    private static async Task Denied(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql,string state)
    {
        await Run(db,tx,"SAVEPOINT roomsvc_binding_probe");
        string? error=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException e){error=e.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT roomsvc_binding_probe");
            await Run(db,tx,"RELEASE SAVEPOINT roomsvc_binding_probe");
        }
        Assert.Equal(state,error);
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
