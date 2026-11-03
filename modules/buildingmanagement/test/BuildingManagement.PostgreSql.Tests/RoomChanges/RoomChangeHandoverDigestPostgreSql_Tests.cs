using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Migrations;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Tests the actual domain canonical H6 hash against the SQL function and
/// negative direct-SQL evidence injection. Disposable transaction-local
/// schema ONLY; no application user data or persistent PG migrations.
/// </summary>
public sealed class RoomChangeHandoverDigestPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HPhysicalHandoverDigestIntegrity().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HPhysicalHandoverDigestIntegrity().DownOperations
            .OfType<SqlOperation>()).Sql;

    private static readonly DateTime Created =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    // No FK or independent meter-identity triggers are duplicated here.
    // They are already checked in TRFPG12-14 and the full migration chain.
    private const string Tables = """
        CREATE TABLE "BuildingManagementRoomChangeHandoverEvidence" (
          "Id" uuid PRIMARY KEY,
          "RequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL,
          "SourceRoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "HandoverDate" date NOT NULL,
          "ElectricityMeterId" uuid NOT NULL,
          "ElectricityReadingId" uuid NOT NULL,
          "ElectricityFinalReading" numeric(18,3) NOT NULL,
          "WaterMeterId" uuid NOT NULL,
          "WaterReadingId" uuid NOT NULL,
          "WaterFinalReading" numeric(18,3) NOT NULL,
          "InspectionEvidenceReference" varchar(500) NOT NULL,
          "SignedHandoverReference" varchar(500) NOT NULL,
          "AttestedByUserId" uuid NOT NULL,
          "AttestedAtUtc" timestamptz NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL
        );
        """;

    [Fact]
    public async Task TRFPG25_Domain_Canonical_Handover_Hash_Matches_PostgreSQL()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var r = Create();
            await Insert(db, tx, r);
            Assert.Equal(r.EvidenceSha256, await StringScalar(db, tx,
                $"""
                SELECT "BM_CalculateRoomChangeHandoverDigest"(h)
                  FROM "BuildingManagementRoomChangeHandoverEvidence" h
                 WHERE h."Id"='{r.Id:D}'
                """));
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeHandoverEvidence" """));
        });
    }

    [Fact]
    public async Task TRFPG26_ValidHex_But_Forged_Document_Or_Meter_Amount_Denied()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var r = Create();
            await Denied(db, tx, () => Insert(db, tx, r,
                hash: new string('0', 64)));
            await Denied(db, tx, () => Insert(db, tx, r,
                inspection: "FORGED-INSPECTION-SIGNED-REF-0001"));
            await Denied(db, tx, () => Insert(db, tx, r,
                electricity: r.ElectricityFinalReading + 1m));
            Assert.Equal(0L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeHandoverEvidence" """));
        });
    }

    [Fact]
    public async Task TRFPG27_Preexisting_Invalid_Audit_Blocks_Migration_Not_Rewritten()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var r = Create();
            await Run(db, tx, Down);
            await Insert(db, tx, r, hash: new string('0', 64));
            await Denied(db, tx, () => Run(db, tx, Up));
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeHandoverEvidence" """));
            await Run(db, tx,
                """DELETE FROM "BuildingManagementRoomChangeHandoverEvidence" """);
            await Run(db, tx, Up);
            await Insert(db, tx, r);
            await Denied(db, tx, () => Run(db, tx, Down));
        });
    }

    [Fact]
    public async Task TRFPG28_Unsigned_ReadCommitted_Handover_Is_Rejected()
    {
        await Scratch(IsolationLevel.ReadCommitted, async (db, tx) =>
        {
            await Denied(db, tx, () => Insert(db, tx, Create()));
        });
    }

    [Fact]
    public async Task TRFPG29_Empty_Schema_Can_Downgrade_And_Reapply()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            await Run(db, tx, Down);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger
                 WHERE NOT tgisinternal
                   AND tgname='TR_BM_RoomChangeHandover_DigestIntegrity'
                """));
            await Run(db, tx, Up);
            await Denied(db, tx, () => Insert(db, tx, Create(),
                atUtc: Created.AddHours(-1)));
        });
    }

    private static RoomChangeHandoverEvidenceRecord Create()
    {
        var room = Guid.NewGuid();
        var request = RoomChangeRequest.Submit(
            Guid.NewGuid(), Guid.NewGuid(), room, Guid.NewGuid(),
            Guid.NewGuid(), new DateOnly(2026, 11, 30),
            "Documented signed transfer request",
            Guid.NewGuid(), Created,
            ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var electricity = new UtilityMeter(Guid.NewGuid(), room,
            UtilityType.Electricity, "E-METER-H6-SQL-001", 0m,
            new DateTime(2026, 10, 1));
        var water = new UtilityMeter(Guid.NewGuid(), room,
            UtilityType.Water, "W-METER-H6-SQL-001", 0m,
            new DateTime(2026, 10, 1));
        var er = new MeterReading(Guid.NewGuid(), electricity.Id,
            2026, 11, 10m, 50.125m, new DateTime(2026, 11, 30));
        var wr = new MeterReading(Guid.NewGuid(), water.Id,
            2026, 11, 5m, 19.250m, new DateTime(2026, 11, 30));
        return RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), request, electricity, er, water, wr,
            "SIGNED-H6-INSPECTION-2026-001",
            "SIGNED-H6-HANDOVER-2026-001",
            Guid.NewGuid(), Created.AddSeconds(1).AddTicks(1_234_560));
    }

    private static async Task Insert(
        NpgsqlConnection db, NpgsqlTransaction tx,
        RoomChangeHandoverEvidenceRecord r,
        string? hash = null, string? inspection = null,
        decimal? electricity = null, DateTime? atUtc = null)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeHandoverEvidence"
            ("Id","RequestId","SourceContractId","SourceRoomId","BuildingId",
             "HandoverDate","ElectricityMeterId","ElectricityReadingId",
             "ElectricityFinalReading","WaterMeterId","WaterReadingId",
             "WaterFinalReading","InspectionEvidenceReference",
             "SignedHandoverReference","AttestedByUserId","AttestedAtUtc",
             "EvidenceSha256")
            VALUES (@id,@request,@contract,@room,@building,@date,@em,@er,@ef,
                    @wm,@wr,@wf,@inspection,@signed,@actor,@at,@hash)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("request", r.RequestId);
        cmd.Parameters.AddWithValue("contract", r.SourceContractId);
        cmd.Parameters.AddWithValue("room", r.SourceRoomId);
        cmd.Parameters.AddWithValue("building", r.BuildingId);
        cmd.Parameters.AddWithValue("date", r.HandoverDate);
        cmd.Parameters.AddWithValue("em", r.ElectricityMeterId);
        cmd.Parameters.AddWithValue("er", r.ElectricityReadingId);
        cmd.Parameters.AddWithValue("ef", electricity ?? r.ElectricityFinalReading);
        cmd.Parameters.AddWithValue("wm", r.WaterMeterId);
        cmd.Parameters.AddWithValue("wr", r.WaterReadingId);
        cmd.Parameters.AddWithValue("wf", r.WaterFinalReading);
        cmd.Parameters.AddWithValue("inspection",
            inspection ?? r.InspectionEvidenceReference);
        cmd.Parameters.AddWithValue("signed", r.SignedHandoverReference);
        cmd.Parameters.AddWithValue("actor", r.AttestedByUserId);
        cmd.Parameters.AddWithValue("at", atUtc ?? r.AttestedAtUtc);
        cmd.Parameters.AddWithValue("hash", hash ?? r.EvidenceSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Scratch(IsolationLevel level,
        Func<NpgsqlConnection,NpgsqlTransaction,Task> test)
    {
        var safe = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(safe) {
                Pooling=false, CommandTimeout=45
            }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_h6_digest_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync(level))
        {
            try
            {
                await Run(db, tx, $"""CREATE SCHEMA "{schema}" """);
                await Run(db, tx, $"""SET LOCAL search_path TO "{schema}" """);
                await Run(db, tx, Tables);
                await Run(db, tx, Up);
                await test(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, Func<Task> action)
    {
        await Run(db, tx, "SAVEPOINT h6_digest_probe");
        string? state = null;
        try { await action(); }
        catch (PostgresException e) { state = e.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT h6_digest_probe");
            await Run(db, tx, "RELEASE SAVEPOINT h6_digest_probe");
        }
        Assert.Equal("23514", state);
    }

    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection db,
        NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> StringScalar(NpgsqlConnection db,
        NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
