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
/// Real PostgreSQL probes for the D06 signed-scan and download-audit
/// trails. Disposable transaction + random schema only; nothing outside
/// the allowlisted integration database is touched.
/// </summary>
public sealed class RoomChangeD06SignedScanPostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HD06SignedScanAndDownloadAudit().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HD06SignedScanAndDownloadAudit().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Stubs = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementRoomChangeRequests" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeEvidenceArchives" (
          "Id" uuid PRIMARY KEY,
          "RequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "Kind" integer NOT NULL,
          "SourceEvidenceSha256" varchar(64) NOT NULL,
          "RecordSha256" varchar(64) NOT NULL,
          "ArchivedAtUtc" timestamptz NOT NULL);
        """;

    private sealed record Fixture(
        Guid Handover, Guid Contract, Guid Building, Guid Request);

    private static async Task<Fixture> Seed(
        NpgsqlConnection db, NpgsqlTransaction tx, int kind = 4)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid());
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementContracts"("Id") VALUES('{f.Contract:D}');
            INSERT INTO "BuildingManagementRoomChangeRequests"("Id")
              VALUES('{f.Request:D}');
            INSERT INTO "BuildingManagementRoomChangeEvidenceArchives"
             ("Id","RequestId","SourceContractId","BuildingId","Kind",
              "SourceEvidenceSha256","RecordSha256","ArchivedAtUtc")
            VALUES('{f.Handover:D}','{f.Request:D}','{f.Contract:D}',
              '{f.Building:D}',{kind},repeat('a',64),repeat('b',64),
              now() - interval '2 hours');
            """);
        return f;
    }

    private static string Scan(
        Fixture f, Guid? handover = null, Guid? request = null,
        string? sha = null, string? media = null, int bytes = 2048,
        string? vault = null, string? uploadedAt = null)
        => $"""
            INSERT INTO "BuildingManagementRoomChangeSignedHandoverScans"
             ("Id","RequestId","SourceContractId","BuildingId","HandoverArchiveId",
              "HandoverEvidenceSha256","HandoverDocumentSha256","MediaType",
              "ByteLength","VaultReference","UploadedByUserId","UploadedAtUtc",
              "EvidenceSha256")
            VALUES('{Guid.NewGuid():D}',
              '{(request is null ? f.Request : request.Value):D}',
              '{f.Contract:D}','{f.Building:D}',
              '{(handover is null ? f.Handover : handover.Value):D}',
              repeat('b',64),'{sha ?? new string('c', 64)}',
              '{media ?? "application/pdf"}',{bytes},
              '{vault ?? Guid.NewGuid().ToString("N")}',
              '{Guid.NewGuid():D}',{uploadedAt ?? "now() - interval '1 hour'"},
              repeat('d',64))
            """;

    private static string Access(
        Fixture f, Guid? contract = null, string? purpose = null,
        string? sha = null, int bytes = 4096, string? at = null)
        => $"""
            INSERT INTO "BuildingManagementContractDocumentAccess"
             ("Id","ContractId","BuildingId","AccessedByUserId","AccessedAtUtc",
              "Purpose","DeliveredDocumentSha256","DeliveredByteLength",
              "EvidenceSha256")
            VALUES('{Guid.NewGuid():D}',
              '{(contract is null ? f.Contract : contract.Value):D}',
              '{f.Building:D}','{Guid.NewGuid():D}',
              {at ?? "now() - interval '1 hour'"},
              '{purpose ?? "Owner exported the contract document for review"}',
              '{sha ?? new string('c', 64)}',{bytes},repeat('e',64))
            """;

    [Fact]
    public async Task TRFD06PG01_Scan_Is_Recorded_And_Append_Only()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Scan(f));
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeSignedHandoverScans"
                   SET "MediaType"='image/png'
                """, "23514");
            await Denied(db, tx,
                """
                DELETE FROM "BuildingManagementRoomChangeSignedHandoverScans"
                """, "23514");
            await Denied(db, tx,
                """
                TRUNCATE "BuildingManagementRoomChangeSignedHandoverScans"
                """, "23514");
        });
    }

    [Fact]
    public async Task TRFD06PG02_Scan_Requires_Serializable_Isolation()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Scan(f));
        });
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx, Scan(f), "23514");
        }, IsolationLevel.RepeatableRead);
    }

    [Fact]
    public async Task TRFD06PG03_Scan_Must_Bind_To_The_Handover_Archive()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx, Scan(f, handover: Guid.NewGuid()), "23514");
            // A different request on the same handover is refused.
            var other = await Seed(db, tx);
            await Denied(db, tx,
                Scan(f, request: other.Request), "23514");
        });
    }

    [Fact]
    public async Task TRFD06PG04_Only_A_Three_Party_Handover_May_Carry_A_Scan()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx, kind: 1);
            await Denied(db, tx, Scan(f), "23514");
        });
    }

    [Fact]
    public async Task TRFD06PG05_One_Scan_Per_Handover_And_Per_Request()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Scan(f));
            await Denied(db, tx, Scan(f), "23505");
        });
    }

    [Fact]
    public async Task TRFD06PG06_Scan_Cannot_Be_Dated_In_The_Future()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx,
                Scan(f, uploadedAt: "'2999-01-01T00:00:00Z'"), "23514");
            await Denied(db, tx,
                Scan(f, uploadedAt: "now() - interval '3 hours'"), "23514");
        });
    }

    [Fact]
    public async Task TRFD06PG07_Only_Pdf_Png_And_Jpeg_Are_Storable()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx, Scan(f, media: "text/plain"), "23514");
            await Run(db, tx, Scan(f, media: "image/jpeg"));
        });
    }

    [Fact]
    public async Task TRFD06PG08_Download_Audit_Is_Append_Only_And_Audited()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Access(f));
            await Denied(db, tx, """
                UPDATE "BuildingManagementContractDocumentAccess"
                   SET "Purpose"='Rewritten download purpose after the fact'
                """, "23514");
            await Denied(db, tx,
                """
                TRUNCATE "BuildingManagementContractDocumentAccess"
                """, "23514");
            Assert.Equal(1L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementContractDocumentAccess"
                """));
        });
    }

    [Fact]
    public async Task TRFD06PG09_Download_Audit_Needs_A_Live_Contract_And_Purpose()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx,
                Access(f, contract: Guid.NewGuid()), "23514");
            await Denied(db, tx, Access(f, purpose: "too short"), "23514");
            await Denied(db, tx,
                Access(f, at: "'2999-01-01T00:00:00Z'"), "23514");
            await Run(db, tx, Access(f));
        });
    }

    [Fact]
    public async Task TRFD06PG10_Downgrade_With_Evidence_Is_Refused_Empty_Allowed()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Scan(f));
            await Denied(db, tx, Down, "23514");
        });
        await Scratch(async (db, tx, _) =>
        {
            await Run(db, tx, Down);
            Assert.Equal(0L, await Scalar(db, tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomChangeSignedHandoverScans'
                   AND relnamespace=current_schema()::regnamespace
                """));
            await Run(db, tx, Up);
            var f = await Seed(db, tx);
            await Run(db, tx, Scan(f));
            await Run(db, tx, Access(f));
        });
    }

    private static async Task Scratch(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> test,
        IsolationLevel isolation = IsolationLevel.Serializable)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
            { Pooling = false, CommandTimeout = 30 }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_d06_scan_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db, tx, $"""CREATE SCHEMA "{schema}" """);
                await Run(db, tx, $"""SET LOCAL search_path TO "{schema}" """);
                await Run(db, tx, Stubs);
                await Run(db, tx, Up);
                await test(db, tx, schema);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null, $"""
            SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'
            """));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql, string code)
    {
        await Run(db, tx, "SAVEPOINT d06_probe");
        string? actual = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException e) { actual = e.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT d06_probe");
            await Run(db, tx, "RELEASE SAVEPOINT d06_probe");
        }
        Assert.Equal(code, actual);
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
