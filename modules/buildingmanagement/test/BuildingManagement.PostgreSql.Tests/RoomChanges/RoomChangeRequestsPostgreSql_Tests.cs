using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// P2H intake-only migration tested against a random, transaction-rolled-back
/// scratch schema. Never connects to or migrates a production database.
/// </summary>
public sealed class RoomChangeRequestsPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2HRoomChangeRequests().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2HRoomChangeRequests().DownOperations.OfType<SqlOperation>()).Sql;

    private const string SchemaSql = """
        CREATE TABLE "BuildingManagementFloors" (
            "Id" uuid PRIMARY KEY, "BuildingId" uuid NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementRooms" (
            "Id" uuid PRIMARY KEY, "FloorId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementContracts" (
            "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementRoomReservations" (
            "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        """;

    [Fact]
    public async Task TRFPG01_Foreign_Target_Duplicate_Open_And_Rewrite_Are_Denied()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var building = Guid.NewGuid();
            var foreign = Guid.NewGuid();
            var floor = Guid.NewGuid();
            var foreignFloor = Guid.NewGuid();
            var source = Guid.NewGuid();
            var target = Guid.NewGuid();
            var foreignTarget = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var first = Guid.NewGuid();

            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementFloors"("Id","BuildingId")
                  VALUES('{floor:D}','{building:D}'),
                        ('{foreignFloor:D}','{foreign:D}');
                INSERT INTO "BuildingManagementRooms"("Id","FloorId","Status")
                  VALUES ('{source:D}','{floor:D}',2),
                         ('{target:D}','{floor:D}',1),
                         ('{foreignTarget:D}','{foreignFloor:D}',1);
                INSERT INTO "BuildingManagementContracts"("Id","RoomId","Status")
                  VALUES ('{contract:D}','{source:D}',2);
                """);

            string Insert(Guid id, Guid destination, int status = 0) => $"""
                INSERT INTO "BuildingManagementRoomChangeRequests"
                ("Id","SourceContractId","SourceRoomId","TargetRoomId",
                 "BuildingId","RequestedDate","Reason","Status",
                 "RequestedByUserId","CreatedAtUtc")
                VALUES ('{id:D}','{contract:D}','{source:D}',
                        '{destination:D}','{building:D}','2026-12-31',
                        'Verified change request for review',{status},
                        '{actor:D}','2026-11-01T10:00:00Z')
                """;

            await Denied(db, tx, Insert(Guid.NewGuid(), foreignTarget), "23514");
            await Denied(db, tx, Insert(Guid.NewGuid(), target, 4), "23514");
            await Run(db, tx, Insert(first, target));
            await Denied(db, tx, Insert(Guid.NewGuid(), target), "23505");
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeRequests"
                   SET "SourceRoomId"='{target:D}' WHERE "Id"='{first:D}'
                """, "23514");

            // Request is recorded but never creates occupancy or deposit
            // disposition, even when an operator has held it for policy.
            await Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeRequests"
                   SET "Status"=1,"ReviewedByUserId"='{actor:D}',
                       "ReviewedAtUtc"='2026-11-02T10:00:00Z',
                       "ReviewNotes"='Awaiting deposit transfer policy'
                 WHERE "Id"='{first:D}'
                """);
            await Denied(db, tx, Insert(Guid.NewGuid(), target), "23505");
            await Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeRequests"
                   SET "Status"=2,"ReviewedByUserId"='{actor:D}',
                       "ReviewedAtUtc"='2026-11-03T10:00:00Z',
                       "ReviewNotes"='Rejected by building manager'
                 WHERE "Id"='{first:D}'
                """);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeRequests"
                   SET "Status"=0, "ReviewedByUserId"=NULL,
                       "ReviewedAtUtc"=NULL,"ReviewNotes"=NULL
                 WHERE "Id"='{first:D}'
                """, "23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeRequests"
                 WHERE "Id"='{first:D}'
                """, "23514");
            Assert.Equal(2L, await Scalar(db, tx,
                $"SELECT \"Status\" FROM \"BuildingManagementRoomChangeRequests\" WHERE \"Id\"='{first:D}'"));
            Assert.Equal(2L, await Scalar(db, tx,
                $"SELECT \"Status\" FROM \"BuildingManagementRooms\" WHERE \"Id\"='{source:D}'"));
            Assert.Equal(1L, await Scalar(db, tx,
                $"SELECT \"Status\" FROM \"BuildingManagementRooms\" WHERE \"Id\"='{target:D}'"));
            await Denied(db, tx, DownSql, "23514");
        });
    }

    [Fact]
    public async Task TRFPG02_Reserved_Or_Pending_Target_And_Open_MoveOut_Are_Denied()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var b = Guid.NewGuid();
            var f = Guid.NewGuid();
            var source = Guid.NewGuid();
            var target = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var actor = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementFloors"("Id","BuildingId")
                  VALUES('{f:D}','{b:D}');
                INSERT INTO "BuildingManagementRooms"("Id","FloorId","Status")
                  VALUES('{source:D}','{f:D}',2),('{target:D}','{f:D}',1);
                INSERT INTO "BuildingManagementContracts"("Id","RoomId","Status")
                  VALUES('{contract:D}','{source:D}',2);
                INSERT INTO "BuildingManagementRoomReservations"("Id","RoomId","Status")
                  VALUES('{Guid.NewGuid():D}','{target:D}',1);
                """);
            string NewRequest() => $"""
                INSERT INTO "BuildingManagementRoomChangeRequests"
                ("Id","SourceContractId","SourceRoomId","TargetRoomId",
                 "BuildingId","RequestedDate","Reason","Status",
                 "RequestedByUserId","CreatedAtUtc")
                VALUES('{Guid.NewGuid():D}','{contract:D}','{source:D}',
                       '{target:D}','{b:D}','2026-12-31',
                       'Transfer reviewed before execution',0,
                       '{actor:D}','2026-11-01T10:00:00Z')
                """;

            await Denied(db, tx, NewRequest(), "23514");
            await Run(db, tx, """
                UPDATE "BuildingManagementRoomReservations"
                   SET "Status"=4
                """);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementMoveOutRequests"
                  ("Id","ContractId","Status")
                VALUES('{Guid.NewGuid():D}','{contract:D}',1)
                """);
            await Denied(db, tx, NewRequest(), "23514");
            await Run(db, tx, """
                UPDATE "BuildingManagementMoveOutRequests"
                   SET "Status"=3
                """);
            await Run(db, tx, NewRequest());
            Assert.Equal(1L, await Scalar(db, tx,
                "SELECT count(*) FROM \"BuildingManagementRoomChangeRequests\""));
        });
    }

    [Fact]
    public async Task TRFPG03_EmptyHistory_Allows_Rollback_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_RoomChangeRequest_Guard'
                """));
            await Run(db, tx, UpSql);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_RoomChangeRequest_Guard'
                """));
        });
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> test)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        { Pooling = false, CommandTimeout = 30 }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_roomchange_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await Run(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await Run(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await Run(db, tx, SchemaSql);
                await Run(db, tx, UpSql);
                await test(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql, string expected)
    {
        await Run(db, tx, "SAVEPOINT roomchange_probe");
        string? errorCode = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException exception) { errorCode = exception.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT roomchange_probe");
            await Run(db, tx, "RELEASE SAVEPOINT roomchange_probe");
        }
        Assert.Equal(expected, errorCode);
    }

    private static async Task Run(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
