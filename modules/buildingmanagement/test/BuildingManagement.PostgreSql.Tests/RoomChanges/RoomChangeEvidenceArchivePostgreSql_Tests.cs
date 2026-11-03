using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Standalone SERIALIZABLE PG test for all four immutable proof types.
/// The stubbed parents simulate verified source ledgers; test checks SQL
/// bypass resistance and canonical hashes, not real signature authenticity.
/// Schema and inserted rows are entirely rolled back.
/// </summary>
public sealed class RoomChangeEvidenceArchivePostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HDocumentaryEvidenceArchive().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HDocumentaryEvidenceArchive().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly DateTime At =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string ShaA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Stub = """
        CREATE TABLE "BuildingManagementContracts"(
          "Id" uuid PRIMARY KEY
        );
        CREATE TABLE "BuildingManagementRoomChangeRequests"(
          "Id" uuid PRIMARY KEY,
          "SourceContractId" uuid NOT NULL,
          "SourceRoomId" uuid NOT NULL,
          "TargetRoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "RequestedDate" date NOT NULL,
          "Status" integer NOT NULL,
          "CreatedAtUtc" timestamptz NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeDayPricingRuleCandidates"(
          "Id" uuid PRIMARY KEY, "RequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL, "BuildingId" uuid NOT NULL,
          "PolicyReviewId" uuid NOT NULL, "EvidenceSha256" varchar(64) NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeBusinessPolicyReviews"(
          "Id" uuid PRIMARY KEY, "ReviewedByUserId" uuid NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeHandoverEvidence"(
          "Id" uuid PRIMARY KEY, "RequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL, "BuildingId" uuid NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL
        );
        CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger"(
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "RoomId" uuid NOT NULL,
          "BillingYear" integer NOT NULL,
          "BillingMonth" integer NOT NULL,
          "CandidateSha256" varchar(64) NOT NULL
        );
        """;

    private sealed record F(
        Guid RequestId, Guid ContractId, Guid RoomId,
        Guid TargetRoomId, Guid BuildingId, Guid ReviewerId,
        Guid PricingRuleId, Guid PolicyReviewId,
        Guid InvoiceId, Guid HandoverId);

    private sealed record Row(
        Guid Id, Guid RequestId, Guid SourceContractId, Guid BuildingId,
        int Kind, Guid EvidenceId, string SourceEvidenceSha256,
        Guid ParentEvidenceId, string ParentEvidenceSha256,
        string EvidenceJson, string EvidenceJsonSha256,
        Guid ArchivedByUserId, DateTime ArchivedAtUtc,
        string RecordSha256);

    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static Row Make(F f, int kind,
        Guid? request = null, Guid? contract = null,
        string? parentSha = null, string? jsonOverride = null)
    {
        var req = request ?? f.RequestId;
        var source = contract ?? f.ContractId;
        var actor = f.ReviewerId;
        var evidence = Guid.NewGuid();
        var parent = kind switch
        {
            1 => f.PricingRuleId,
            2 => f.InvoiceId,
            3 => Guid.NewGuid(), // packet: only documentary candidate
            4 => f.HandoverId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var ps = parentSha ?? ShaA;
        object body = kind switch
        {
            1 => new { Id = evidence, EvidenceSha256 = ShaB,
                RequestId = req, OwnerUserId = actor,
                PricingRuleId = parent, PricingRuleSha256 = ps },
            2 => new { Id = evidence, EvidenceSha256 = ShaB,
                ContractId = source, BuildingId = f.BuildingId,
                ClassifiedByUserId = actor,
                IssuedInvoiceId = parent, OriginalInvoiceSha256 = ps },
            3 => new { Id = evidence, EvidenceSha256 = ShaB,
                RequestId = req, SourceContractId = source,
                IndependentBankReviewerId = actor,
                TargetContractId = f.TargetRoomId,
                FinancialPacketId = parent, FinancialPacketSha256 = ps },
            4 => new { Id = evidence, EvidenceSha256 = ShaB,
                RequestId = req, OwnerSignerId = actor,
                HandoverId = parent, PhysicalHandoverSha256 = ps },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var json = jsonOverride ?? JsonSerializer.Serialize(body);
        var hashJson = Sha(json);
        var id = Guid.NewGuid();
        var canonical = string.Join("\n", new[] {
            id.ToString("D"), req.ToString("D"),
            source.ToString("D"), f.BuildingId.ToString("D"),
            kind.ToString(CultureInfo.InvariantCulture),
            evidence.ToString("D"), ShaB, parent.ToString("D"), ps,
            hashJson, actor.ToString("D"),
            At.ToString("O", CultureInfo.InvariantCulture)
        });
        return new Row(id,req,source,f.BuildingId,kind,evidence,ShaB,
            parent,ps,json,hashJson,actor,At,Sha(canonical));
    }

    [Fact]
    public async Task TRFARCHPG01_All_Four_Evidence_Kinds_Have_Identical_Domain_SQL_SHA()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            for (var kind = 1; kind <= 4; kind++)
            {
                var row = Make(f,kind);
                await Insert(db,tx,row);
                var actual = await Value(db,tx,$"""
                    SELECT "BM_CalculateRoomChangeEvidenceArchiveSha"(r)
                    FROM "BuildingManagementRoomChangeEvidenceArchives" r
                    WHERE r."Id"='{row.Id:D}'
                    """);
                Assert.Equal(row.RecordSha256,actual);
            }
            Assert.Equal("4", await Value(db,tx,
                """SELECT count(*)::text FROM "BuildingManagementRoomChangeEvidenceArchives" """));
        });
    }

    [Fact]
    public async Task TRFARCHPG02_Forged_JSON_Hash_And_Parent_Proof_Are_Denied()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            var valid = Make(f,1);
            await Denied(db,tx,()=>Insert(db,tx,valid,
                json:valid.EvidenceJson+" "), "23514");
            await Denied(db,tx,()=>Insert(db,tx,valid,
                hash:new string('0',64)), "23514");
            var wrongParent = Make(f,4,parentSha:ShaB);
            await Denied(db,tx,()=>Insert(db,tx,wrongParent), "23514");
        });
    }

    [Fact]
    public async Task TRFARCHPG03_Foreign_Request_Contract_And_Source_Are_Denied()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            var foreign = Make(f,1,request:Guid.NewGuid());
            await Denied(db,tx,()=>Insert(db,tx,foreign), "23503");
            var crossContract = Make(f,2,contract:Guid.NewGuid());
            await Denied(db,tx,()=>Insert(db,tx,crossContract), "23503");
            var foreignParent = Make(f,1,parentSha:ShaB);
            await Denied(db,tx,()=>Insert(db,tx,foreignParent), "23514");
        });
    }

    [Fact]
    public async Task TRFARCHPG04_Immutable_History_And_Unique_References()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            var first = Make(f,1);
            await Insert(db,tx,first);
            await Denied(db,tx,()=>Insert(db,tx,Make(f,1)), "23505");
            await Denied(db,tx,()=>Run(db,tx,$"""
                DELETE FROM "BuildingManagementRoomChangeEvidenceArchives"
                WHERE "Id"='{first.Id:D}'
                """), "23514");
            await Denied(db,tx,()=>Run(db,tx,$"""
                UPDATE "BuildingManagementRoomChangeEvidenceArchives"
                SET "SourceEvidenceSha256"='{ShaA}' WHERE "Id"='{first.Id:D}'
                """), "23514");
            await Denied(db,tx,()=>Run(db,tx,
                """TRUNCATE "BuildingManagementRoomChangeEvidenceArchives" """),
                "23514");
            await Denied(db,tx,()=>Run(db,tx,Down), "23514");
        });
    }

    [Fact]
    public async Task TRFARCHPG05_ReadCommitted_Documentary_Write_Is_Refused()
    {
        await Scratch(IsolationLevel.ReadCommitted, async (db,tx,f) =>
        {
            await Denied(db,tx,()=>Insert(db,tx,Make(f,1)), "23514");
        });
    }

    [Fact]
    public async Task TRFARCHPG06_Empty_Rollback_And_Reapply_Preserve_Integrity()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Run(db,tx,Down);
            await Run(db,tx,Up);
            await Insert(db,tx,Make(f,1));
        });
    }

    [Fact]
    public async Task TRFARCHPG07_Same_Request_Different_D03_Corrections_Are_Allowed()
    {
        await Scratch(IsolationLevel.Serializable, async (db,tx,f) =>
        {
            await Insert(db,tx,Make(f,2));
            // One original invoice can have multiple independent approved
            // correction records, so D03 has no per-kind single-row lock.
            // RequestParent index still prevents duplicate archived parent.
            await Denied(db,tx,()=>Insert(db,tx,Make(f,2)), "23505");
        });
    }

    private static async Task Insert(NpgsqlConnection db,
        NpgsqlTransaction tx,Row row,string? json=null,string? hash=null)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeEvidenceArchives"
             ("Id","RequestId","SourceContractId","BuildingId","Kind",
              "EvidenceId","SourceEvidenceSha256","ParentEvidenceId",
              "ParentEvidenceSha256","EvidenceJson","EvidenceJsonSha256",
              "ArchivedByUserId","ArchivedAtUtc","RecordSha256")
            VALUES (@id,@request,@contract,@building,@kind,@proof,
              @proofHash,@parent,@parentHash,@json,@jsonHash,
              @actor,@at,@recordHash)
            """,db,tx);
        cmd.Parameters.AddWithValue("id",row.Id);
        cmd.Parameters.AddWithValue("request",row.RequestId);
        cmd.Parameters.AddWithValue("contract",row.SourceContractId);
        cmd.Parameters.AddWithValue("building",row.BuildingId);
        cmd.Parameters.AddWithValue("kind",row.Kind);
        cmd.Parameters.AddWithValue("proof",row.EvidenceId);
        cmd.Parameters.AddWithValue("proofHash",row.SourceEvidenceSha256);
        cmd.Parameters.AddWithValue("parent",row.ParentEvidenceId);
        cmd.Parameters.AddWithValue("parentHash",row.ParentEvidenceSha256);
        cmd.Parameters.AddWithValue("json",json??row.EvidenceJson);
        cmd.Parameters.AddWithValue("jsonHash",row.EvidenceJsonSha256);
        cmd.Parameters.AddWithValue("actor",row.ArchivedByUserId);
        cmd.Parameters.AddWithValue("at",row.ArchivedAtUtc);
        cmd.Parameters.AddWithValue("recordHash",hash??row.RecordSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Scratch(IsolationLevel isolation,
        Func<NpgsqlConnection,NpgsqlTransaction,F,Task> run)
    {
        var conn=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(conn)
        { Pooling=false, CommandTimeout=45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_2h_evidence_"+Guid.NewGuid().ToString("N");
        await using(var tx=await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,Stub);
                await Run(db,tx,Up);
                var f=new F(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
                    Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
                await Run(db,tx,$"""
                    INSERT INTO "BuildingManagementContracts" VALUES
                    ('{f.ContractId:D}');
                    INSERT INTO "BuildingManagementRoomChangeRequests" VALUES
                    ('{f.RequestId:D}','{f.ContractId:D}','{f.RoomId:D}',
                     '{f.TargetRoomId:D}','{f.BuildingId:D}','2026-11-15',0,
                     '2026-11-01T09:00:00Z');
                    INSERT INTO "BuildingManagementRoomChangeBusinessPolicyReviews" VALUES
                    ('{f.PolicyReviewId:D}','{f.ReviewerId:D}');
                    INSERT INTO "BuildingManagementRoomChangeDayPricingRuleCandidates" VALUES
                    ('{f.PricingRuleId:D}','{f.RequestId:D}','{f.ContractId:D}',
                     '{f.BuildingId:D}','{f.PolicyReviewId:D}','{ShaA}');
                    INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger" VALUES
                    ('{f.InvoiceId:D}','{f.ContractId:D}','{f.BuildingId:D}',
                     '{f.RoomId:D}',2026,11,'{ShaA}');
                    INSERT INTO "BuildingManagementRoomChangeHandoverEvidence" VALUES
                    ('{f.HandoverId:D}','{f.RequestId:D}','{f.ContractId:D}',
                     '{f.BuildingId:D}','{ShaA}');
                    """);
                await run(db,tx,f);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal("0",await Value(db,null,
            $"SELECT count(*)::text FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,NpgsqlTransaction tx,
        Func<Task> action,string sqlState)
    {
        await Run(db,tx,"SAVEPOINT archive_probe");
        string? actual=null;
        try { await action(); }
        catch(PostgresException e) { actual=e.SqlState; }
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT archive_probe");
            await Run(db,tx,"RELEASE SAVEPOINT archive_probe");
        }
        Assert.Equal(sqlState,actual);
    }

    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Value(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }
}
