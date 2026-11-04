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
/// Isolated PostgreSQL probes against the REAL additive D03 room-tariff
/// migration. A random schema is created and rolled back: no application
/// table, production database, payment or invoice is modified.
/// </summary>
public sealed class RoomScopedServiceTariffPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HRoomScopedServiceTariffVersions().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HRoomScopedServiceTariffVersions().DownOperations
            .OfType<SqlOperation>()).Sql;
    private const string Sha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Stub = """
        CREATE TABLE "BuildingManagementRooms" (
          "Id" uuid PRIMARY KEY,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        """;

    private static string Insert(
        Guid room, Guid service, int version, string from,
        decimal price = 150000m, string? through = null,
        int unit = 1, int charging = 2,
        string approvedUtc = "2026-10-01T00:00:00Z")
    {
        var end = through is null ? "NULL" : $"'{through}'";
        return $"""
            INSERT INTO "BuildingManagementRoomScopedServiceTariffVersions"
             ("Id","RoomId","ServiceId","Version","UnitPriceVnd",
              "BillingUnit","ChargingRule","EffectiveFrom","EffectiveThrough",
              "ApprovedByUserId","ApprovedAtUtc","SignedPolicyReference",
              "EvidenceSha256")
            VALUES ('{Guid.NewGuid():D}','{room:D}','{service:D}',{version},
              {price}, {unit}, {charging}, '{from}',{end},
              '{Guid.NewGuid():D}','{approvedUtc}',
              'SIGNED-OWNER-PER-ROOM-SERVICE-TARIFF-2026','{Sha}')
            """;
    }

    [Fact]
    public async Task TRFSVCPG01_Same_Service_Room_301_And_302_Have_Independent_Rates()
    {
        await Scratch(async(db,tx,room) =>
        {
            var second=Guid.NewGuid();
            var service=Guid.NewGuid();
            await Run(db,tx,$"""
                INSERT INTO "BuildingManagementRooms"("Id") VALUES('{second:D}')
                """);
            await Run(db,tx,Insert(room,service,1,"2026-11-01",150000m));
            await Run(db,tx,Insert(second,service,1,"2026-11-01",200000m));
            Assert.Equal(2L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementRoomScopedServiceTariffVersions" """));
            Assert.Equal(150000L,await Scalar(db,tx,$"""
                SELECT "UnitPriceVnd"
                FROM "BuildingManagementRoomScopedServiceTariffVersions"
                WHERE "RoomId"='{room:D}' AND "ServiceId"='{service:D}'
                """));
            Assert.Equal(200000L,await Scalar(db,tx,$"""
                SELECT "UnitPriceVnd"
                FROM "BuildingManagementRoomScopedServiceTariffVersions"
                WHERE "RoomId"='{second:D}' AND "ServiceId"='{service:D}'
                """));
        });
    }

    [Fact]
    public async Task TRFSVCPG02_New_Prospective_Version_Preserves_Old_Original()
    {
        await Scratch(async(db,tx,room) =>
        {
            var service=Guid.NewGuid();
            await Run(db,tx,Insert(room,service,1,"2026-11-01",150000m));
            await Run(db,tx,Insert(room,service,2,"2026-12-01",200000m));
            Assert.Equal(2L,await Scalar(db,tx,
                """SELECT count(*) FROM "BuildingManagementRoomScopedServiceTariffVersions" """));
            Assert.Equal(150000L,await Scalar(db,tx,$"""
                SELECT "UnitPriceVnd"
                FROM "BuildingManagementRoomScopedServiceTariffVersions"
                WHERE "RoomId"='{room:D}' AND "ServiceId"='{service:D}'
                  AND "EffectiveFrom"<='2026-11-15'::date
                ORDER BY "EffectiveFrom" DESC LIMIT 1
                """));
            Assert.Equal(200000L,await Scalar(db,tx,$"""
                SELECT "UnitPriceVnd"
                FROM "BuildingManagementRoomScopedServiceTariffVersions"
                WHERE "RoomId"='{room:D}' AND "ServiceId"='{service:D}'
                  AND "EffectiveFrom"<='2026-12-15'::date
                ORDER BY "EffectiveFrom" DESC LIMIT 1
                """));
        });
    }

    [Fact]
    public async Task TRFSVCPG03_Concurrent_Replay_And_NonSequential_Version_Fail_Closed()
    {
        await Scratch(async(db,tx,room) =>
        {
            var service=Guid.NewGuid();
            await Run(db,tx,Insert(room,service,1,"2026-11-01"));
            await Denied(db,tx,Insert(room,service,1,"2026-12-01"),"23514");
            await Denied(db,tx,Insert(room,service,3,"2026-12-01"),"23514");
            await Denied(db,tx,Insert(room,service,2,"2026-11-01"),"23514");
            await Run(db,tx,Insert(room,service,2,"2026-12-01"));
            await Denied(db,tx,Insert(room,service,3,"2026-12-01"),"23514");
        });
    }

    [Fact]
    public async Task TRFSVCPG04_Finite_Old_Rate_Cannot_Overlap_Next_Version()
    {
        await Scratch(async(db,tx,room) =>
        {
            var service=Guid.NewGuid();
            await Run(db,tx,Insert(room,service,1,"2026-11-01",
                through:"2026-11-30"));
            await Denied(db,tx,Insert(room,service,2,"2026-11-15"),"23514");
            await Run(db,tx,Insert(room,service,2,"2026-12-01"));
        });
    }

    [Fact]
    public async Task TRFSVCPG05_Immutable_History_And_NoTruncate_And_Down_Fences()
    {
        await Scratch(async(db,tx,room) =>
        {
            await Denied(db,tx,
                """TRUNCATE "BuildingManagementRoomScopedServiceTariffVersions" """,
                "23514");
            await Run(db,tx,Insert(room,Guid.NewGuid(),1,"2026-11-01"));
            await Denied(db,tx,
                """
                UPDATE "BuildingManagementRoomScopedServiceTariffVersions"
                   SET "UnitPriceVnd"=1
                ""","23514");
            await Denied(db,tx,
                """DELETE FROM "BuildingManagementRoomScopedServiceTariffVersions" """,
                "23514");
            await Denied(db,tx,Down,"23514");
        });
    }

    [Fact]
    public async Task TRFSVCPG06_Deleted_Or_Missing_Room_And_Retroactive_Price_Denied()
    {
        await Scratch(async(db,tx,room) =>
        {
            await Denied(db,tx,Insert(Guid.NewGuid(),Guid.NewGuid(),
                1,"2026-11-01"),"23514");
            await Denied(db,tx,Insert(room,Guid.NewGuid(),
                1,"2026-09-30"),"23514");
            await Run(db,tx,$"""
                UPDATE "BuildingManagementRooms" SET "IsDeleted"=TRUE
                WHERE "Id"='{room:D}'
                """);
            await Denied(db,tx,Insert(room,Guid.NewGuid(),
                1,"2026-11-01"),"23514");
        });
    }

    [Fact]
    public async Task TRFSVCPG07_Invalid_Service_Billing_And_Evidence_Are_Denied()
    {
        await Scratch(async(db,tx,room) =>
        {
            var service=Guid.NewGuid();
            await Denied(db,tx,Insert(room,service,1,"2026-11-01",
                unit:3,charging:1),"23514");
            await Denied(db,tx,Insert(room,service,1,"2026-11-01",
                price:-1m),"23514");
            await Run(db,tx,Insert(room,service,1,"2026-11-01",
                unit:3,charging:3));
        });
    }

    [Fact]
    public async Task TRFSVCPG08_Empty_Up_Down_And_Reupgrade_Are_Allowed()
    {
        await Scratch(async(db,tx,room) =>
        {
            await Run(db,tx,Down);
            Assert.Equal(0L,await Scalar(db,tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomScopedServiceTariffVersions'
                   AND relnamespace=current_schema()::regnamespace
                """));
            await Run(db,tx,Up);
            Assert.Equal(1L,await Scalar(db,tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomScopedServiceTariffVersions'
                   AND relnamespace=current_schema()::regnamespace
                """));
        });
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Task> work)
    {
        var cs=new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            { Pooling=false,CommandTimeout=30 }.ConnectionString;
        await using var db=new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_roomsvc_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stub);
                await Run(db,tx,Up);
                var room=Guid.NewGuid();
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementRooms"("Id") VALUES('{room:D}')
                    """);
                await work(db,tx,room);
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
        await Run(db,tx,"SAVEPOINT roomsvc_probe");
        string? error=null;
        try {await Run(db,tx,sql);}
        catch(PostgresException e){error=e.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT roomsvc_probe");
            await Run(db,tx,"RELEASE SAVEPOINT roomsvc_probe");
        }
        Assert.Equal(code,error);
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
