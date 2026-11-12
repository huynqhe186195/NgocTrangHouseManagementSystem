using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Migrations;
using BuildingManagement.MoveOuts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Phase 2G.5 archived-handover proof is enforced by real PostgreSQL even
/// when the caller bypasses the domain factory. A disposable, rollback-only
/// schema makes no persistent application or integration schema changes.
/// </summary>
public sealed class Phase2G5RoomDispositionArchiveIntegrityPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2G5RoomDispositionArchiveIntegrity().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2G5RoomDispositionArchiveIntegrity().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string FixtureSql = """
        CREATE TABLE "BuildingManagementMoveOutRoomDispositions" (
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL,
          "MoveOutRequestId" uuid NOT NULL,
          "RoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "CloseoutApprovalId" uuid NOT NULL,
          "CompletedByUserId" uuid NOT NULL,
          "CompletedAtUtc" timestamptz NOT NULL,
          "ResultRoomStatus" integer NOT NULL,
          "FutureReservationId" uuid NULL,
          "FutureSignedContractId" uuid NULL,
          "RequiresMaintenance" boolean NOT NULL,
          "MaintenanceReason" text NULL,
          "MaintenanceEvidenceReference" text NULL,
          "HandoverEvidenceReference" text NOT NULL,
          "DispositionJson" text NOT NULL,
          "DispositionSha256" varchar(64) NOT NULL
        );
        """;

    [Fact]
    public async Task CMOUTPG51_Canonical_Available_Reserved_And_Maintenance_Archives_Accepted()
    {
        foreach (var state in new[] { "Available", "Reserved", "Maintenance" })
        {
            await Scratch(async (db, tx) =>
            {
                var record = Create(state);
                await Run(db, tx, Insert(record));
                Assert.Equal(1L, await Scalar(db, tx,
                    """SELECT count(*) FROM "BuildingManagementMoveOutRoomDispositions" """));
                await Denied(db, tx, DownSql);
            });
        }
    }

    [Fact]
    public async Task CMOUTPG52_Tampered_Hash_Or_Foreign_Archived_Contract_Fails_Closed()
    {
        await Scratch(async (db, tx) =>
        {
            var record = Create("Available");
            await Denied(db, tx, Insert(record,
                jsonOverride: record.DispositionJson + " "));

            await Denied(db, tx, Insert(record,
                hashOverride: new string('0', 64)));

            var altered = JsonNode.Parse(record.DispositionJson)!.AsObject();
            altered["ContractId"] = Guid.NewGuid().ToString("D");
            var forgedJson = altered.ToJsonString();
            await Denied(db, tx, Insert(record,
                jsonOverride: forgedJson, hashOverride: Hash(forgedJson)));

            Assert.Equal(0L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementMoveOutRoomDispositions" """));
        });
    }

    [Fact]
    public async Task CMOUTPG53_Missing_Handover_Proof_Or_Tampered_Room_State_Denied()
    {
        await Scratch(async (db, tx) =>
        {
            var record = Create("Reserved");
            var missing = JsonNode.Parse(record.DispositionJson)!.AsObject();
            missing.Remove("HandoverEvidenceReference");
            var forgedJson = missing.ToJsonString();
            await Denied(db, tx, Insert(record,
                jsonOverride: forgedJson, hashOverride: Hash(forgedJson)));

            await Denied(db, tx, Insert(record,
                roomStatusOverride: RoomStatus.Available));
            Assert.Equal(0L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementMoveOutRoomDispositions" """));
        });
    }

    [Fact]
    public async Task CMOUTPG54_Empty_Downgrade_And_Reapply_Safe_But_Signed_History_Blocks()
    {
        await Scratch(async (db, tx) =>
        {
            await Run(db, tx, DownSql);
            await Run(db, tx, UpSql);

            await Run(db, tx, Insert(Create("Available")));
            await Denied(db, tx, DownSql);
        });
    }

    private static MoveOutRoomDispositionRecord Create(string outcome)
    {
        var reserve = outcome == "Reserved";
        var maintenance = outcome == "Maintenance";
        return MoveOutRoomDispositionRecord.Complete(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            RoomStatus.Occupied, 0m,
            reserve
                ? new[] { new MoveOutRoomReservationFact(Guid.NewGuid(),
                    RoomReservationStatus.Reserved) }
                : Array.Empty<MoveOutRoomReservationFact>(),
            Array.Empty<MoveOutRoomSignedContractFact>(),
            maintenance,
            maintenance ? "Signed inspection requires repairs" : null,
            maintenance ? "SIGNED-REPAIR-INSPECTION-0001" : null,
            "SIGNED-ROOM-HANDOVER-0001");
    }

    private static string Insert(
        MoveOutRoomDispositionRecord record,
        string? jsonOverride = null,
        string? hashOverride = null,
        RoomStatus? roomStatusOverride = null)
        => $"""
        INSERT INTO "BuildingManagementMoveOutRoomDispositions"
          ("Id","ContractId","MoveOutRequestId","RoomId","BuildingId",
           "CloseoutApprovalId","CompletedByUserId","CompletedAtUtc",
           "ResultRoomStatus","FutureReservationId","FutureSignedContractId",
           "RequiresMaintenance","MaintenanceReason",
           "MaintenanceEvidenceReference","HandoverEvidenceReference",
           "DispositionJson","DispositionSha256")
        VALUES (
           '{record.Id:D}','{record.ContractId:D}',
           '{record.MoveOutRequestId:D}','{record.RoomId:D}',
           '{record.BuildingId:D}','{record.CloseoutApprovalId:D}',
           '{record.CompletedByUserId:D}','{record.CompletedAtUtc:O}',
           {(int)(roomStatusOverride ?? record.ResultRoomStatus)},
           {GuidSql(record.FutureReservationId)},
           {GuidSql(record.FutureSignedContractId)},
           {(record.RequiresMaintenance ? "TRUE" : "FALSE")},
           {TextSql(record.MaintenanceReason)},
           {TextSql(record.MaintenanceEvidenceReference)},
           {TextSql(record.HandoverEvidenceReference)},
           {TextSql(jsonOverride ?? record.DispositionJson)},
           {TextSql(hashOverride ?? record.DispositionSha256)}
        )
        """;

    private static string GuidSql(Guid? value)
        => value.HasValue ? $"'{value.Value:D}'" : "NULL";

    private static string TextSql(string? value)
        => value is null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static async Task Scratch(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> scenario)
    {
        var cs = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(cs)
            { Pooling = false, CommandTimeout = 45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_cmout_archive_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            try
            {
                await Run(db, tx, $"""CREATE SCHEMA "{schema}" """);
                await Run(db, tx, $"""SET LOCAL search_path TO "{schema}" """);
                await Run(db, tx, FixtureSql);
                await Run(db, tx, UpSql);
                await scenario(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }

        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await Run(db, tx, "SAVEPOINT cmout_archive_negative");
        string? state = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException ex) { state = ex.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT cmout_archive_negative");
            await Run(db, tx, "RELEASE SAVEPOINT cmout_archive_negative");
        }
        Assert.Equal("23514", state);
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
