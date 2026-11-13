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
/// Real PostgreSQL probes for the H4 obligation dispute lane. Runs only in a
/// disposable transaction and random schema of the allowlisted integration
/// database. No contract, cash, bank or signed obligation is altered.
/// </summary>
public sealed class RoomChangeH4ObligationDisputePostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HH4ObligationDisputes().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HH4ObligationDisputes().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Stubs = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementRoomChangeH4Obligations" (
          "Id" uuid PRIMARY KEY,
          "RequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL,
          "TargetContractId" uuid NOT NULL,
          "Kind" integer NOT NULL,
          "AmountVnd" numeric(18,0) NOT NULL,
          "DueDateVietnam" date NULL);
        CREATE TABLE "BuildingManagementRoomChangeH4SettlementPostings" (
          "Id" uuid PRIMARY KEY,
          "ObligationId" uuid NOT NULL);
        """;

    private sealed record Fixture(Guid Obligation, Guid Contract, Guid Building);

    private static async Task<Fixture> Seed(
        NpgsqlConnection db, NpgsqlTransaction tx, bool settled = false)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementContracts"("Id")
              VALUES('{f.Contract:D}');
            INSERT INTO "BuildingManagementRoomChangeH4Obligations"
             ("Id","RequestId","SourceContractId","TargetContractId",
              "Kind","AmountVnd","DueDateVietnam")
            VALUES('{f.Obligation:D}','{Guid.NewGuid():D}','{Guid.NewGuid():D}',
              '{f.Contract:D}',1,1000000,'2026-12-15');
            """);
        if (settled)
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementRoomChangeH4SettlementPostings"
                 ("Id","ObligationId")
                VALUES('{Guid.NewGuid():D}','{f.Obligation:D}')
                """);
        return f;
    }

    // Timestamps are SQL expressions so the guard's clock comparison stays
    // meaningful on any run date; callers pass quoted literals to override.
    private static string Dispute(
        Fixture f, Guid? id = null, Guid? obligation = null,
        Guid? contract = null, string? reason = null,
        string? evidence = null, string? openedAt = null)
        => $"""
            INSERT INTO "BuildingManagementRoomChangeH4ObligationDisputes"
             ("Id","ObligationId","ContractId","BuildingId","OpenedByUserId",
              "OpenedAtUtc","Reason","EvidenceReference")
            VALUES('{(id is null ? Guid.NewGuid() : id.Value):D}',
              '{(obligation is null ? f.Obligation : obligation.Value):D}',
              '{(contract is null ? f.Contract : contract.Value):D}',
              '{f.Building:D}','{Guid.NewGuid():D}',
              {openedAt ?? "now() - interval '2 hours'"},
              '{reason ?? "Tenant contests the recorded top-up original amount"}',
              '{evidence ?? "SIGNED-TENANT-DISPUTE-LETTER-2026-11"}')
            """;

    private static string Resolution(
        Guid disputeId, Guid contract, Guid? id = null,
        bool accepted = true, Guid? dispute = null,
        string? signed = null, string? resolvedAt = null)
        => $"""
            INSERT INTO "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
             ("Id","DisputeId","ContractId","OwnerId","ResolvedAtUtc",
              "ObligationAccepted","SignedOwnerResolutionReference")
            VALUES('{(id is null ? Guid.NewGuid() : id.Value):D}',
              '{(dispute is null ? disputeId : dispute.Value):D}',
              '{contract:D}','{Guid.NewGuid():D}',
              {resolvedAt ?? "now() - interval '1 hour'"},
              {accepted.ToString().ToLowerInvariant()},
              '{signed ?? "OWNER-SIGNED-DISPUTE-RESOLUTION-2026"}')
            """;

    private static async Task<Guid> OpenDispute(
        NpgsqlConnection db, NpgsqlTransaction tx, Fixture f)
    {
        var id = Guid.NewGuid();
        await Run(db, tx, Dispute(f, id: id));
        return id;
    }

    [Fact]
    public async Task TRFH4DSPG01_Open_Dispute_Is_Recorded_And_Append_Only()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            var dispute = await OpenDispute(db, tx, f);
            Assert.Equal(1L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeH4ObligationDisputes"
                """));
            await Denied(db, tx,
                $"""
                UPDATE "BuildingManagementRoomChangeH4ObligationDisputes"
                   SET "Reason"='Rewritten after the fact dispute reason'
                 WHERE "Id"='{dispute:D}'
                """, "23514");
            await Denied(db, tx,
                $"""
                DELETE FROM "BuildingManagementRoomChangeH4ObligationDisputes"
                 WHERE "Id"='{dispute:D}'
                """, "23514");
            // One statement, because the resolution table references the
            // dispute table; the truncate guard fires on each.
            await Denied(db, tx,
                """
                TRUNCATE "BuildingManagementRoomChangeH4ObligationDisputes",
                         "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                """, "23514");
        });
    }

    [Fact]
    public async Task TRFH4DSPG02_Dispute_Requires_Serializable_Isolation()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await OpenDispute(db, tx, f);
        });
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx, Dispute(f), "23514");
        }, IsolationLevel.RepeatableRead);
    }

    [Fact]
    public async Task TRFH4DSPG03_Dispute_Must_Match_The_Obligation_Target_Contract()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            var other = Guid.NewGuid();
            await Run(db, tx,
                $"""INSERT INTO "BuildingManagementContracts"("Id") VALUES('{other:D}')""");
            await Denied(db, tx, Dispute(f, contract: other), "23514");
            // Absent obligation is refused by the guard, before any FK check.
            await Denied(db, tx, Dispute(f, obligation: Guid.NewGuid()), "23514");
            await Run(db, tx, Dispute(f));
        });
    }

    [Fact]
    public async Task TRFH4DSPG04_Settled_Obligation_Cannot_Be_Disputed()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx, settled: true);
            await Denied(db, tx, Dispute(f), "23514");
        });
    }

    [Fact]
    public async Task TRFH4DSPG05_Only_One_Dispute_Per_Obligation()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Run(db, tx, Dispute(f));
            await Denied(db, tx, Dispute(f), "23505");
            Assert.Equal(1L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeH4ObligationDisputes"
                """));
        });
    }

    [Fact]
    public async Task TRFH4DSPG06_Resolution_Must_Match_Its_Own_Dispute_And_Contract()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            var dispute = await OpenDispute(db, tx, f);
            await Denied(db, tx, Resolution(dispute, Guid.NewGuid()), "23514");
            await Run(db, tx, Resolution(dispute, f.Contract));
            await Denied(db, tx,
                Resolution(dispute, f.Contract, accepted: false), "23505");
            Assert.Equal(1L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                """));
        });
    }

    [Fact]
    public async Task TRFH4DSPG07_Thin_Reason_Or_Evidence_Is_Refused()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await Denied(db, tx, Dispute(f, reason: "too short"), "23514");
            await Denied(db, tx, Dispute(f, evidence: "too short"), "23514");
            await Denied(db, tx, Dispute(f,
                openedAt: "'2999-01-01T00:00:00Z'"), "23514");
            await Run(db, tx, Dispute(f));
        });
    }

    [Fact]
    public async Task TRFH4DSPG08_Downgrade_With_Retained_Evidence_Is_Refused()
    {
        await Scratch(async (db, tx, _) =>
        {
            var f = await Seed(db, tx);
            await OpenDispute(db, tx, f);
            await Denied(db, tx, Down, "23514");
        });
    }

    [Fact]
    public async Task TRFH4DSPG09_Empty_Downgrade_And_Reupgrade_Allowed()
    {
        await Scratch(async (db, tx, _) =>
        {
            await Run(db, tx, Down);
            Assert.Equal(0L, await Scalar(db, tx,
                """
                SELECT count(*) FROM pg_class
                 WHERE relname='BuildingManagementRoomChangeH4ObligationDisputes'
                   AND relnamespace=current_schema()::regnamespace
                """));
            await Run(db, tx, Up);
            var f = await Seed(db, tx);
            var dispute = await OpenDispute(db, tx, f);
            await Run(db, tx, Resolution(dispute, f.Contract));
        });
    }

    [Fact]
    public async Task TRFH4DSPG10_Accepted_And_Rejected_Resolutions_Are_Both_Recorded()
    {
        await Scratch(async (db, tx, _) =>
        {
            var a = await Seed(db, tx);
            await Run(db, tx, Resolution(
                await OpenDispute(db, tx, a), a.Contract, accepted: true));
            var b = await Seed(db, tx);
            await Run(db, tx, Resolution(
                await OpenDispute(db, tx, b), b.Contract, accepted: false));
            Assert.Equal(2L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                """));
            Assert.Equal(1L, await Scalar(db, tx,
                """
                SELECT count(*) FROM "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
                 WHERE "ObligationAccepted"=false
                """));
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
        var schema = "bm_h4_dispute_" + Guid.NewGuid().ToString("N");
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
        await Run(db, tx, "SAVEPOINT h4_dispute_probe");
        string? actual = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException e) { actual = e.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT h4_dispute_probe");
            await Run(db, tx, "RELEASE SAVEPOINT h4_dispute_probe");
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
