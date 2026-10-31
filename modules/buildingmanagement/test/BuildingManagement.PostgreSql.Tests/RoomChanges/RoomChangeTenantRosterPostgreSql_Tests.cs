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
/// PostgreSQL 16 real DDL, SQL validation and concurrent membership guard.
/// Everything runs inside random schemas on explicitly authorized scratch DB.
/// </summary>
public sealed class RoomChangeTenantRosterPostgreSql_Tests
{
    private static readonly string RequestsUp = Assert.Single(
        new Phase2HRoomChangeRequests().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string RosterUp = Assert.Single(
        new Phase2HTenantRosterSnapshots().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string RosterDown = Assert.Single(
        new Phase2HTenantRosterSnapshots().DownOperations.OfType<SqlOperation>()).Sql;
    private static readonly string PolicyUp = Assert.Single(
        new Phase2HBusinessPolicyProposals().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string PolicyDown = Assert.Single(
        new Phase2HBusinessPolicyProposals().DownOperations.OfType<SqlOperation>()).Sql;
    private static readonly string PolicyReviewUp = Assert.Single(
        new Phase2HIndependentPolicyReviews().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string PolicyReviewDown = Assert.Single(
        new Phase2HIndependentPolicyReviews().DownOperations.OfType<SqlOperation>()).Sql;
    private static readonly string HandoverUp = Assert.Single(
        new Phase2HPhysicalHandoverEvidence().UpOperations.OfType<SqlOperation>()).Sql;
    private static readonly string HandoverDown = Assert.Single(
        new Phase2HPhysicalHandoverEvidence().DownOperations.OfType<SqlOperation>()).Sql;
    private static readonly DateTime Created = new(
        2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

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
        CREATE TABLE "BuildingManagementContractTenants" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "TenantId" uuid NOT NULL, "Role" integer NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementRoomReservations" (
            "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementMoveOutRequests" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "Status" integer NOT NULL, "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementUtilityMeters" (
            "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
            "UtilityType" integer NOT NULL, "InstalledDate" date NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        CREATE TABLE "BuildingManagementMeterReadings" (
            "Id" uuid PRIMARY KEY, "UtilityMeterId" uuid NOT NULL,
            "BillingYear" integer NOT NULL, "BillingMonth" integer NOT NULL,
            "PreviousReading" numeric(18,3) NOT NULL,
            "CurrentReading" numeric(18,3) NOT NULL,
            "ReadingDate" date NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE);
        """;

    [Fact]
    public async Task TRFPG06_Valid_Snapshot_Is_Immutable_Scoped_And_RollbackProtected()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var fixture = await SeedAsync(db, tx);
            var record = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), fixture.Request, fixture.Parties,
                Guid.NewGuid(), Created.AddSeconds(1));
            await InsertSnapshot(db, tx, record);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
                """));
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeTenantRosterSnapshots"
                   SET "RosterCount"=1 WHERE "Id"='{record.Id:D}'
                """, "23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
                 WHERE "Id"='{record.Id:D}'
                """, "23514");
            await Denied(db, tx, RosterDown, "23514");
            // No PII or financial fields in the immutable archival payload.
            Assert.DoesNotContain("phone", record.RosterJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("deposit", record.RosterJson,
                StringComparison.OrdinalIgnoreCase);
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG07_Mismatched_Party_List_Even_With_Valid_SHA_Is_Denied()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var fixture = await SeedAsync(db, tx);
            var fake = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), fixture.Request,
                new[] { new RoomChangeRosterMember(
                    Guid.NewGuid(), ContractTenantRole.PrimaryTenant) },
                Guid.NewGuid(), Created.AddSeconds(1));
            await DeniedInsert(db, tx, fake, "23514");
            var real = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), fixture.Request, fixture.Parties,
                Guid.NewGuid(), Created.AddSeconds(1));
            await InsertSnapshot(db, tx, real);
            await Denied(db, tx, $"""
                INSERT INTO "BuildingManagementRoomChangeTenantRosterSnapshots"
                ("Id","RoomChangeRequestId","SourceContractId","SourceRoomId",
                 "BuildingId","CapturedByUserId","CapturedAtUtc",
                 "RosterCount","RosterJson","RosterSha256")
                SELECT '{Guid.NewGuid():D}',"RoomChangeRequestId","SourceContractId",
                       "SourceRoomId","BuildingId","CapturedByUserId",
                       "CapturedAtUtc","RosterCount","RosterJson","RosterSha256"
                  FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
                 WHERE "Id"='{real.Id:D}'
                """, "23505");
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG08_Snapshot_Requires_Serializable_Uow()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var f = await SeedAsync(db, tx);
            var record = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), f.Request, f.Parties,
                Guid.NewGuid(), Created.AddSeconds(1));
            await DeniedInsert(db, tx, record, "23514");
        }, IsolationLevel.ReadCommitted);
    }

    [Fact]
    public async Task TRFPG09_Empty_History_Can_Rollback_And_Reupgrade()
    {
        await ScratchAsync(async (db, tx) =>
        {
            await Run(db, tx, HandoverDown);
            await Run(db, tx, PolicyReviewDown);
            await Run(db, tx, PolicyDown);
            await Run(db, tx, RosterDown);
            Assert.Equal(0L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_RoomChangeRoster_Guard'
                """));
            await Run(db, tx, RosterUp);
            await Run(db, tx, PolicyUp);
            await Run(db, tx, PolicyReviewUp);
            await Run(db, tx, HandoverUp);
            Assert.Equal(1L, await Scalar(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                 AND tgname='TR_BM_RoomChangeRoster_Guard'
                """));
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG10_OI07_Policy_Is_Immutable_Unique_And_RosterScoped()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var f = await SeedAsync(db, tx);
            var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), f.Request, f.Parties,
                Guid.NewGuid(), Created.AddSeconds(1));
            await InsertSnapshot(db, tx, roster);
            var policy = RoomChangeBusinessPolicyRecord.Propose(
                Guid.NewGuid(), f.Request, roster,
                RoomChangeContractTreatment.NewContract,
                RoomChangePricingTreatment.FullOldMonthNewNextMonth,
                RoomChangeDepositTreatment.CarryAllWithNoDifference,
                RoomChangeTenantTreatment.AllCurrentTenants,
                "SIGNED-CONTRACT-POLICY-REF-001",
                "SIGNED-PRICING-POLICY-REF-001",
                "SIGNED-DEPOSIT-POLICY-REF-001",
                "SIGNED-TENANT-CONSENT-REF-001",
                Guid.NewGuid(), Created.AddSeconds(2));
            await InsertPolicy(db, tx, policy);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeBusinessPolicies"
                   SET "DepositTreatment"=2 WHERE "Id"='{policy.Id:D}'
                """, "23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeBusinessPolicies"
                 WHERE "Id"='{policy.Id:D}'
                """, "23514");
            await Denied(db, tx, PolicyDown, "23514");
            var duplicate = RoomChangeBusinessPolicyRecord.Propose(
                Guid.NewGuid(), f.Request, roster,
                RoomChangeContractTreatment.VersionedAddendum,
                RoomChangePricingTreatment.AgreedDayProration,
                RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
                RoomChangeTenantTreatment.ConsentingSubset,
                "SIGNED-CONTRACT-POLICY-REF-002",
                "SIGNED-PRICING-POLICY-REF-002",
                "SIGNED-DEPOSIT-POLICY-REF-002",
                "SIGNED-TENANT-CONSENT-REF-002",
                Guid.NewGuid(), Created.AddSeconds(3));
            await Run(db, tx, "SAVEPOINT oi07_duplicate");
            string? state = null;
            try { await InsertPolicy(db, tx, duplicate); }
            catch (PostgresException ex) { state = ex.SqlState; }
            finally
            {
                await Run(db, tx, "ROLLBACK TO SAVEPOINT oi07_duplicate");
                await Run(db, tx, "RELEASE SAVEPOINT oi07_duplicate");
            }
            Assert.Equal("23505", state);
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeBusinessPolicies" """));
            // Nothing can mutate occupancy or deposit by inserting the plan.
            Assert.Equal(2L, await Scalar(db, tx,
                $"""SELECT "Status" FROM "BuildingManagementRooms" WHERE "Id"='{f.Request.SourceRoomId:D}'"""));
            Assert.Equal(1L, await Scalar(db, tx,
                $"""SELECT "Status" FROM "BuildingManagementRooms" WHERE "Id"='{f.Request.TargetRoomId:D}'"""));
            Assert.Equal(2L, await Scalar(db, tx,
                $"""SELECT "Status" FROM "BuildingManagementContracts" WHERE "Id"='{f.Request.SourceContractId:D}'"""));
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG11_Independent_OI07_Review_Is_Immutable_Scoped_And_Unique()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var f = await SeedAsync(db, tx);
            var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
                Guid.NewGuid(), f.Request, f.Parties,
                Guid.NewGuid(), Created.AddSeconds(1));
            await InsertSnapshot(db, tx, roster);
            var proposer = Guid.NewGuid();
            var policy = RoomChangeBusinessPolicyRecord.Propose(
                Guid.NewGuid(), f.Request, roster,
                RoomChangeContractTreatment.NewContract,
                RoomChangePricingTreatment.FullOldMonthNewNextMonth,
                RoomChangeDepositTreatment.CarryAllWithNoDifference,
                RoomChangeTenantTreatment.AllCurrentTenants,
                "SIGNED-CONTRACT-POLICY-REF-001",
                "SIGNED-PRICING-POLICY-REF-001",
                "SIGNED-DEPOSIT-POLICY-REF-001",
                "SIGNED-TENANT-CONSENT-REF-001",
                proposer, Created.AddSeconds(2));
            await InsertPolicy(db, tx, policy);
            var review = RoomChangeBusinessPolicyReviewRecord.Review(
                Guid.NewGuid(), policy, f.Request, roster,
                Guid.NewGuid(), Created.AddSeconds(3), true,
                "SIGNED-ROOT-REVIEW-001",
                "Reviewed policy choice; transfer blocked pending evidence");
            await InsertReview(db, tx, review);
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeBusinessPolicyReviews"
                   SET "Accepted"=FALSE WHERE "Id"='{review.Id:D}'
                """, "23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
                 WHERE "Id"='{review.Id:D}'
                """, "23514");
            await Denied(db, tx, PolicyReviewDown, "23514");
            await Run(db, tx, "SAVEPOINT oi07_review_duplicate");
            string? state = null;
            try
            {
                await InsertReview(db, tx,
                    RoomChangeBusinessPolicyReviewRecord.Review(
                        Guid.NewGuid(), policy, f.Request, roster,
                        Guid.NewGuid(), Created.AddSeconds(4), false,
                        "SIGNED-REJECTION-REPLAY-001",
                        "A second root review must not replace an acceptance"));
            }
            catch (PostgresException ex) { state = ex.SqlState; }
            finally
            {
                await Run(db, tx, "ROLLBACK TO SAVEPOINT oi07_review_duplicate");
                await Run(db, tx, "RELEASE SAVEPOINT oi07_review_duplicate");
            }
            Assert.Equal("23505", state);
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeBusinessPolicyReviews" """));
            Assert.Equal(2L, await Scalar(db, tx,
                $"""SELECT "Status" FROM "BuildingManagementContracts" WHERE "Id"='{f.Request.SourceContractId:D}'"""));
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG12_Physical_Handover_Anchors_Exact_Meter_Source_And_Freezes_Drift()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var (request, e, er, w, wr) = await SeedSignedHandoverAsync(db, tx);
            var handover = RoomChangeHandoverEvidenceRecord.Attest(
                Guid.NewGuid(), request, e, er, w, wr,
                "SIGNED-INSPECTION-PG-2026-001",
                "SIGNED-HANDOVER-PG-2026-001",
                Guid.NewGuid(), Created.AddSeconds(4));
            await InsertHandover(db, tx, handover);
            Assert.Equal(1L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeHandoverEvidence" """));
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementMeterReadings"
                   SET "CurrentReading"=999
                 WHERE "Id"='{er.Id:D}'
                """, "23514");
            await Denied(db, tx, $"""
                UPDATE "BuildingManagementUtilityMeters"
                   SET "RoomId"='{Guid.NewGuid():D}'
                 WHERE "Id"='{w.Id:D}'
                """, "23514");
            await Denied(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeHandoverEvidence"
                 WHERE "Id"='{handover.Id:D}'
                """, "23514");
            await Denied(db, tx, HandoverDown, "23514");
            Assert.Equal(2L, await Scalar(db, tx,
                $"""SELECT "Status" FROM "BuildingManagementContracts" WHERE "Id"='{request.SourceContractId:D}'"""));
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG13_Handover_Rejects_Forged_Reading_Even_With_Valid_Checksum()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var (request, e, er, w, wr) = await SeedSignedHandoverAsync(db, tx);
            var wrongWater = new MeterReading(Guid.NewGuid(), w.Id, 2026, 12,
                19m, 51m, new DateTime(2026, 12, 31));
            var forged = RoomChangeHandoverEvidenceRecord.Attest(
                Guid.NewGuid(), request, e, er, w, wrongWater,
                "SIGNED-INSPECTION-PG-2026-001",
                "SIGNED-HANDOVER-PG-2026-001",
                Guid.NewGuid(), Created.AddSeconds(4));
            await DeniedInsertHandover(db, tx, forged, "23514");
            var clean = RoomChangeHandoverEvidenceRecord.Attest(
                Guid.NewGuid(), request, e, er, w, wr,
                "SIGNED-INSPECTION-PG-2026-001",
                "SIGNED-HANDOVER-PG-2026-001",
                Guid.NewGuid(), Created.AddSeconds(4));
            await InsertHandover(db, tx, clean);
            var replay = RoomChangeHandoverEvidenceRecord.Attest(
                Guid.NewGuid(), request, e, er, w, wr,
                "SIGNED-INSPECTION-PG-2026-002",
                "SIGNED-HANDOVER-PG-2026-002",
                Guid.NewGuid(), Created.AddSeconds(5));
            await DeniedInsertHandover(db, tx, replay, "23505");
        }, IsolationLevel.Serializable);
    }

    [Fact]
    public async Task TRFPG14_Premature_Handover_Without_Root_Review_Is_Denied()
    {
        await ScratchAsync(async (db, tx) =>
        {
            var f = await SeedAsync(db, tx);
            var (e, er, w, wr) = await SeedMetersAsync(db, tx, f.Request.SourceRoomId);
            var handover = RoomChangeHandoverEvidenceRecord.Attest(
                Guid.NewGuid(), f.Request, e, er, w, wr,
                "SIGNED-INSPECTION-PG-2026-001",
                "SIGNED-HANDOVER-PG-2026-001",
                Guid.NewGuid(), Created.AddSeconds(4));
            await DeniedInsertHandover(db, tx, handover, "23514");
        }, IsolationLevel.Serializable);
    }

    private static async Task<(RoomChangeRequest Request,
        UtilityMeter E, MeterReading Er, UtilityMeter W, MeterReading Wr)>
        SeedSignedHandoverAsync(NpgsqlConnection db, NpgsqlTransaction tx)
    {
        var f = await SeedAsync(db, tx);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), f.Request, f.Parties,
            Guid.NewGuid(), Created.AddSeconds(1));
        await InsertSnapshot(db, tx, roster);
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), f.Request, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.FullOldMonthNewNextMonth,
            RoomChangeDepositTreatment.CarryAllWithNoDifference,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-CONTRACT-POLICY-PG-001", "SIGNED-PRICING-POLICY-PG-001",
            "SIGNED-DEPOSIT-POLICY-PG-001", "SIGNED-TENANT-CONSENT-PG-001",
            Guid.NewGuid(), Created.AddSeconds(2));
        await InsertPolicy(db, tx, policy);
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, f.Request, roster,
            Guid.NewGuid(), Created.AddSeconds(3), true,
            "SIGNED-ROOT-REVIEW-PG-001",
            "Root accepted plan only; financial checks remain");
        await InsertReview(db, tx, review);
        var meters = await SeedMetersAsync(db, tx, f.Request.SourceRoomId);
        return (f.Request, meters.E, meters.Er, meters.W, meters.Wr);
    }

    private static async Task<(UtilityMeter E, MeterReading Er,
        UtilityMeter W, MeterReading Wr)> SeedMetersAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid source)
    {
        var e = new UtilityMeter(Guid.NewGuid(), source, UtilityType.Electricity,
            "E-METER-SIGNED-001", 0m, new DateTime(2026, 10, 1));
        var w = new UtilityMeter(Guid.NewGuid(), source, UtilityType.Water,
            "W-METER-SIGNED-001", 0m, new DateTime(2026, 10, 1));
        var er = new MeterReading(Guid.NewGuid(), e.Id, 2026, 12,
            10m, 50m, new DateTime(2026, 12, 31));
        var wr = new MeterReading(Guid.NewGuid(), w.Id, 2026, 12,
            12m, 33m, new DateTime(2026, 12, 31));
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementUtilityMeters"
             ("Id","RoomId","UtilityType","InstalledDate")
             VALUES ('{e.Id:D}','{source:D}',1,'2026-10-01'),
                    ('{w.Id:D}','{source:D}',2,'2026-10-01');
            INSERT INTO "BuildingManagementMeterReadings"
             ("Id","UtilityMeterId","BillingYear","BillingMonth",
              "PreviousReading","CurrentReading","ReadingDate")
             VALUES ('{er.Id:D}','{e.Id:D}',2026,12,10,50,'2026-12-31'),
                    ('{wr.Id:D}','{w.Id:D}',2026,12,12,33,'2026-12-31');
            """);
        return (e, er, w, wr);
    }

    private static async Task DeniedInsertHandover(NpgsqlConnection db,
        NpgsqlTransaction tx, RoomChangeHandoverEvidenceRecord record, string state)
    {
        await Run(db, tx, "SAVEPOINT handover_probe");
        string? actual = null;
        try { await InsertHandover(db, tx, record); }
        catch (PostgresException ex) { actual = ex.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT handover_probe");
            await Run(db, tx, "RELEASE SAVEPOINT handover_probe");
        }
        Assert.Equal(state, actual);
    }

    private static async Task InsertHandover(NpgsqlConnection db,
        NpgsqlTransaction tx, RoomChangeHandoverEvidenceRecord r)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeHandoverEvidence"
             ("Id","RequestId","SourceContractId","SourceRoomId","BuildingId",
              "HandoverDate","ElectricityMeterId","ElectricityReadingId",
              "ElectricityFinalReading","WaterMeterId","WaterReadingId",
              "WaterFinalReading","InspectionEvidenceReference",
              "SignedHandoverReference","AttestedByUserId","AttestedAtUtc",
              "EvidenceSha256")
            VALUES (@id,@request,@contract,@room,@building,@date,
                    @em,@er,@ev,@wm,@wr,@wv,@inspection,@signed,@actor,@at,@hash)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("request", r.RequestId);
        cmd.Parameters.AddWithValue("contract", r.SourceContractId);
        cmd.Parameters.AddWithValue("room", r.SourceRoomId);
        cmd.Parameters.AddWithValue("building", r.BuildingId);
        cmd.Parameters.AddWithValue("date", r.HandoverDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("em", r.ElectricityMeterId);
        cmd.Parameters.AddWithValue("er", r.ElectricityReadingId);
        cmd.Parameters.AddWithValue("ev", r.ElectricityFinalReading);
        cmd.Parameters.AddWithValue("wm", r.WaterMeterId);
        cmd.Parameters.AddWithValue("wr", r.WaterReadingId);
        cmd.Parameters.AddWithValue("wv", r.WaterFinalReading);
        cmd.Parameters.AddWithValue("inspection", r.InspectionEvidenceReference);
        cmd.Parameters.AddWithValue("signed", r.SignedHandoverReference);
        cmd.Parameters.AddWithValue("actor", r.AttestedByUserId);
        cmd.Parameters.AddWithValue("at", r.AttestedAtUtc);
        cmd.Parameters.AddWithValue("hash", r.EvidenceSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertReview(
        NpgsqlConnection db, NpgsqlTransaction tx,
        RoomChangeBusinessPolicyReviewRecord r)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeBusinessPolicyReviews"
              ("Id","PolicyId","RequestId","BuildingId","ProposalSha256",
               "ReviewedByUserId","ReviewedAtUtc","Accepted",
               "SignedReviewReference","Reason")
            VALUES (@id,@policy,@request,@building,@hash,@actor,@at,
                    @accept,@signed,@reason)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("policy", r.PolicyId);
        cmd.Parameters.AddWithValue("request", r.RequestId);
        cmd.Parameters.AddWithValue("building", r.BuildingId);
        cmd.Parameters.AddWithValue("hash", r.ProposalSha256);
        cmd.Parameters.AddWithValue("actor", r.ReviewedByUserId);
        cmd.Parameters.AddWithValue("at", r.ReviewedAtUtc);
        cmd.Parameters.AddWithValue("accept", r.Accepted);
        cmd.Parameters.AddWithValue("signed", r.SignedReviewReference);
        cmd.Parameters.AddWithValue("reason", r.Reason);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertPolicy(
        NpgsqlConnection db, NpgsqlTransaction tx, RoomChangeBusinessPolicyRecord p)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeBusinessPolicies"
              ("Id","RequestId","SourceContractId","BuildingId",
               "RosterSnapshotId","RosterSha256","ContractTreatment",
               "PricingTreatment","DepositTreatment","TenantTreatment",
               "ContractPolicyReference","PricingPolicyReference",
               "DepositPolicyReference","TenantConsentReference",
               "ProposedByUserId","ProposedAtUtc","EvidenceSha256")
            VALUES (@id,@request,@contract,@building,@roster,@rosterhash,
                    @contractChoice,@pricingChoice,@depositChoice,@tenantChoice,
                    @contractRef,@pricingRef,@depositRef,@tenantRef,
                    @actor,@at,@hash)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", p.Id);
        cmd.Parameters.AddWithValue("request", p.RequestId);
        cmd.Parameters.AddWithValue("contract", p.SourceContractId);
        cmd.Parameters.AddWithValue("building", p.BuildingId);
        cmd.Parameters.AddWithValue("roster", p.RosterSnapshotId);
        cmd.Parameters.AddWithValue("rosterhash", p.RosterSha256);
        cmd.Parameters.AddWithValue("contractChoice", (int)p.ContractTreatment);
        cmd.Parameters.AddWithValue("pricingChoice", (int)p.PricingTreatment);
        cmd.Parameters.AddWithValue("depositChoice", (int)p.DepositTreatment);
        cmd.Parameters.AddWithValue("tenantChoice", (int)p.TenantTreatment);
        cmd.Parameters.AddWithValue("contractRef", p.ContractPolicyReference);
        cmd.Parameters.AddWithValue("pricingRef", p.PricingPolicyReference);
        cmd.Parameters.AddWithValue("depositRef", p.DepositPolicyReference);
        cmd.Parameters.AddWithValue("tenantRef", p.TenantConsentReference);
        cmd.Parameters.AddWithValue("actor", p.ProposedByUserId);
        cmd.Parameters.AddWithValue("at", p.ProposedAtUtc);
        cmd.Parameters.AddWithValue("hash", p.EvidenceSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(RoomChangeRequest Request,
        RoomChangeRosterMember[] Parties)> SeedAsync(
        NpgsqlConnection db, NpgsqlTransaction tx)
    {
        var building = Guid.NewGuid();
        var floor = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var contract = Guid.NewGuid();
        var request = RoomChangeRequest.Submit(Guid.NewGuid(), contract,
            source, target, building, new DateOnly(2026, 12, 31),
            "Documented room transfer request", Guid.NewGuid(), Created,
            ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var parties = new[] {
            new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.PrimaryTenant),
            new RoomChangeRosterMember(Guid.NewGuid(), ContractTenantRole.Member)
        };
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementFloors"("Id","BuildingId")
              VALUES('{floor:D}','{building:D}');
            INSERT INTO "BuildingManagementRooms"("Id","FloorId","Status")
              VALUES('{source:D}','{floor:D}',2),('{target:D}','{floor:D}',1);
            INSERT INTO "BuildingManagementContracts"("Id","RoomId","Status")
              VALUES('{contract:D}','{source:D}',2);
            INSERT INTO "BuildingManagementContractTenants"
              ("Id","ContractId","TenantId","Role")
              VALUES('{Guid.NewGuid():D}','{contract:D}',
                     '{parties[0].TenantId:D}',1),
                    ('{Guid.NewGuid():D}','{contract:D}',
                     '{parties[1].TenantId:D}',2);
            INSERT INTO "BuildingManagementRoomChangeRequests"
              ("Id","SourceContractId","SourceRoomId","TargetRoomId",
               "BuildingId","RequestedDate","Reason","Status",
               "RequestedByUserId","CreatedAtUtc")
              VALUES('{request.Id:D}','{contract:D}','{source:D}',
                     '{target:D}','{building:D}','2026-12-31',
                     'Documented room transfer request',0,
                     '{request.RequestedByUserId:D}','2026-11-01T10:00:00Z')
            """);
        return (request, parties);
    }

    private static async Task InsertSnapshot(
        NpgsqlConnection db, NpgsqlTransaction tx,
        RoomChangeTenantRosterSnapshotRecord record)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeTenantRosterSnapshots"
            ("Id","RoomChangeRequestId","SourceContractId","SourceRoomId",
             "BuildingId","CapturedByUserId","CapturedAtUtc",
             "RosterCount","RosterJson","RosterSha256")
            VALUES (@id,@request,@contract,@room,@building,@actor,@at,
                    @count,@json,@sha)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("request", record.RoomChangeRequestId);
        cmd.Parameters.AddWithValue("contract", record.SourceContractId);
        cmd.Parameters.AddWithValue("room", record.SourceRoomId);
        cmd.Parameters.AddWithValue("building", record.BuildingId);
        cmd.Parameters.AddWithValue("actor", record.CapturedByUserId);
        cmd.Parameters.AddWithValue("at", record.CapturedAtUtc);
        cmd.Parameters.AddWithValue("count", record.RosterCount);
        cmd.Parameters.AddWithValue("json", record.RosterJson);
        cmd.Parameters.AddWithValue("sha", record.RosterSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DeniedInsert(
        NpgsqlConnection db, NpgsqlTransaction tx,
        RoomChangeTenantRosterSnapshotRecord record, string state)
    {
        await Run(db, tx, "SAVEPOINT roster_probe");
        string? actual = null;
        try { await InsertSnapshot(db, tx, record); }
        catch (PostgresException ex) { actual = ex.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT roster_probe");
            await Run(db, tx, "RELEASE SAVEPOINT roster_probe");
        }
        Assert.Equal(state, actual);
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> test,
        IsolationLevel isolation)
    {
        var cs = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        { Pooling = false, CommandTimeout = 30 }.ConnectionString;
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_roomchange_roster_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await Run(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await Run(db, tx, SchemaSql);
                await Run(db, tx, RequestsUp);
                await Run(db, tx, RosterUp);
                await Run(db, tx, PolicyUp);
                await Run(db, tx, PolicyReviewUp);
                await Run(db, tx, HandoverUp);
                await test(db, tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await Scalar(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(
        NpgsqlConnection db, NpgsqlTransaction tx,
        string sql, string expected)
    {
        await Run(db, tx, "SAVEPOINT roster_probe");
        string? state = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException ex) { state = ex.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT roster_probe");
            await Run(db, tx, "RELEASE SAVEPOINT roster_probe");
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
