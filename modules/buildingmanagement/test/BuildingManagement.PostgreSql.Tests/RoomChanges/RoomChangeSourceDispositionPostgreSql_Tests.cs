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
/// Transaction-rolled-back PostgreSQL schema: isolated direct SQL probes for
/// the additional Phase 2H source disposition constraint trigger. The old
/// atomic financial/roster gate is independently covered by TRFPG01-17 and
/// the full disposable migration/HTTP rehearsal.
/// </summary>
public sealed class RoomChangeSourceDispositionPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HSourceRoomDispositionCommitGuard().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HSourceRoomDispositionCommitGuard().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Tables = """
        CREATE TABLE "BuildingManagementRooms" (
          "Id" uuid PRIMARY KEY, "Status" integer NOT NULL
        );
        CREATE TABLE "BuildingManagementContracts" (
          "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "Status" integer NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomReservations" (
          "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "Status" integer NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeCompletions" (
          "Id" uuid PRIMARY KEY, "SourceContractId" uuid NOT NULL,
          "SourceRoomId" uuid NOT NULL, "SourceRoomDisposition" integer NOT NULL
        );
        """;

    [Fact]
    public async Task TRFPG18_Clean_Source_Available_Allowed_But_False_Reserved_Denied()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Denied(db, tx, Insert(room, contract, 3), "23514");
            await Run(db, tx, Insert(room, contract, 1));
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeCompletions" """));
        });
    }

    [Fact]
    public async Task TRFPG19_One_Confirmed_Reservation_Requires_Reserved()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Reservation(room, 2));
            await Denied(db, tx, Insert(room, contract, 1), "23514");
            await Denied(db, tx, Insert(room, contract, 4), "23514");
            await Run(db, tx, SetRoom(room, 3));
            await Run(db, tx, Insert(room, contract, 3));
        });
    }

    [Fact]
    public async Task TRFPG20_One_Signed_Future_Contract_Requires_Reserved()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Contract(room, 5));
            await Denied(db, tx, Insert(room, contract, 1), "23514");
            await Run(db, tx, SetRoom(room, 3));
            await Run(db, tx, Insert(room, contract, 3));
        });
    }

    [Fact]
    public async Task TRFPG21_PendingPayment_Source_Reservation_Holds_Even_If_Available()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Reservation(room, 1));
            await Denied(db, tx, Insert(room, contract, 1), "23514");
            await Run(db, tx, SetRoom(room, 4));
            await Denied(db, tx, Insert(room, contract, 4), "23514");
        });
    }

    [Fact]
    public async Task TRFPG22_Multiple_Confirmed_Or_Second_Active_Tenancies_Denied()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Reservation(room, 2));
            await Run(db, tx, Contract(room, 5));
            await Run(db, tx, SetRoom(room, 3));
            await Denied(db, tx, Insert(room, contract, 3), "23514");
        });
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Contract(room, 2));
            await Denied(db, tx, Insert(room, contract, 1), "23514");
        });
    }

    [Fact]
    public async Task TRFPG23_Empty_Downgrade_Reapplies_But_History_Blocks_Rollback()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            await Run(db, tx, Down);
            await Run(db, tx, Up);
            await Run(db, tx, Insert(room, contract, 1));
            await Denied(db, tx, Down, "23514");
        });
    }

    [Fact]
    public async Task TRFPG24_Late_Reservation_Detected_At_Deferred_Commit_Proof()
    {
        await Scratch(async (db, tx, room, contract) =>
        {
            // No client/API preflight can stand in for the COMMIT state.
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementRoomChangeCompletions"
                  VALUES ('{Guid.NewGuid():D}','{contract:D}','{room:D}',1)
                """);
            await Run(db, tx, Reservation(room, 2));
            await Denied(db, tx,
                """SET CONSTRAINTS "TR_BM_RoomChangeSourceDisposition_CommitGate" IMMEDIATE""",
                "23514");
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeCompletions" """));
            // Entire disposable transaction is rolled back by Scratch.
        });
    }

    private static string Reservation(Guid room, int status) => $"""
        INSERT INTO "BuildingManagementRoomReservations"
          ("Id","RoomId","IsDeleted","Status")
        VALUES ('{Guid.NewGuid():D}','{room:D}',FALSE,{status})
        """;

    private static string Contract(Guid room, int status) => $"""
        INSERT INTO "BuildingManagementContracts"
          ("Id","RoomId","IsDeleted","Status")
        VALUES ('{Guid.NewGuid():D}','{room:D}',FALSE,{status})
        """;

    private static string SetRoom(Guid room, int status) => $"""
        UPDATE "BuildingManagementRooms" SET "Status"={status}
        WHERE "Id"='{room:D}'
        """;

    private static string Insert(Guid room, Guid contract, int status) => $"""
        INSERT INTO "BuildingManagementRoomChangeCompletions"
          ("Id","SourceContractId","SourceRoomId","SourceRoomDisposition")
        VALUES ('{Guid.NewGuid():D}','{contract:D}','{room:D}',{status});
        SET CONSTRAINTS "TR_BM_RoomChangeSourceDisposition_CommitGate" IMMEDIATE;
        """;

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Task> test)
    {
        var cs = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(cs)
            { Pooling = false, CommandTimeout = 45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_trf_source_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            try
            {
                await Run(db, tx, $"""CREATE SCHEMA "{schema}" """);
                await Run(db, tx, $"""SET LOCAL search_path TO "{schema}" """);
                await Run(db, tx, Tables);
                await Run(db, tx, Up);
                var room = Guid.NewGuid();
                var contract = Guid.NewGuid();
                await Run(db, tx, $"""
                    INSERT INTO "BuildingManagementRooms" VALUES ('{room:D}',1);
                    INSERT INTO "BuildingManagementContracts"
                      VALUES ('{contract:D}','{room:D}',FALSE,3);
                    """);
                await test(db, tx, room, contract);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,
        NpgsqlTransaction tx, string sql, string expected)
    {
        await Run(db, tx, "SAVEPOINT trf_source_negative");
        string? state = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException ex) { state = ex.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT trf_source_negative");
            await Run(db, tx, "RELEASE SAVEPOINT trf_source_negative");
        }
        Assert.Equal(expected, state);
    }

    private static async Task Run(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
