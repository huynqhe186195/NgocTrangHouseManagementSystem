using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive H6 PostgreSQL authenticity-of-bytes gate: the existing domain
/// produces a SHA-256 of the canonical, typed physical handover evidence,
/// but the original table constraint only checked its 64-hex format.
/// Recompute the exact .NET canonical digest at insertion (and reject
/// pre-existing mismatches on migration), without touching original meter,
/// policy/approval, room, financial or atomic completion gates.
/// A matching digest is NOT independent human-signature validation.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103060000_Phase2HPhysicalHandoverDigestIntegrity")]
public sealed class Phase2HPhysicalHandoverDigestIntegrity : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE FUNCTION "BM_CalculateRoomChangeHandoverDigest"(
          h "BuildingManagementRoomChangeHandoverEvidence"
        )
        RETURNS text LANGUAGE sql AS $canonical$
          SELECT encode(sha256(convert_to(concat_ws(E'\n',
            h."Id"::text,
            h."RequestId"::text,
            h."SourceContractId"::text,
            h."SourceRoomId"::text,
            h."BuildingId"::text,
            h."HandoverDate"::text,
            h."ElectricityMeterId"::text,
            h."ElectricityReadingId"::text,
            h."ElectricityFinalReading"::numeric(18,3)::text,
            h."WaterMeterId"::text,
            h."WaterReadingId"::text,
            h."WaterFinalReading"::numeric(18,3)::text,
            h."InspectionEvidenceReference",
            h."SignedHandoverReference",
            h."AttestedByUserId"::text,
            to_char(h."AttestedAtUtc" AT TIME ZONE 'UTC',
                    'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z'
          ),'UTF8')),'hex');
        $canonical$;

        -- Existing audit history may not silently be grandfathered as valid.
        -- Fail the entire staging migration rather than rewriting evidence.
        DO $legacy$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeHandoverEvidence" h
             WHERE h."EvidenceSha256" IS DISTINCT FROM
                "BM_CalculateRoomChangeHandoverDigest"(h)
          ) THEN
            RAISE EXCEPTION
              'Existing 2H physical handover has invalid canonical digest; audit before migration'
              USING ERRCODE='23514';
          END IF;
        END;
        $legacy$;

        CREATE FUNCTION "BM_VerifyRoomChangeHandoverDigest"()
        RETURNS trigger LANGUAGE plpgsql AS $proof$
        BEGIN
          IF TG_OP <> 'INSERT' OR
             current_setting('transaction_isolation') <> 'serializable' OR
             NEW."EvidenceSha256" IS DISTINCT FROM
                "BM_CalculateRoomChangeHandoverDigest"(NEW)
          THEN
            RAISE EXCEPTION
              'H6 physical handover SHA-256 does not match the signed source fields'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $proof$;

        CREATE TRIGGER "TR_BM_RoomChangeHandover_DigestIntegrity"
          BEFORE INSERT ON "BuildingManagementRoomChangeHandoverEvidence"
          FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRoomChangeHandoverDigest"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeHandoverEvidence"
          IN ACCESS EXCLUSIVE MODE;
        DO $rollback$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeHandoverEvidence" LIMIT 1
          ) THEN
            RAISE EXCEPTION
              'Refuse to remove H6 digest proof with signed handover history'
              USING ERRCODE='23514';
          END IF;
        END;
        $rollback$;

        DROP TRIGGER "TR_BM_RoomChangeHandover_DigestIntegrity"
          ON "BuildingManagementRoomChangeHandoverEvidence";
        DROP FUNCTION "BM_VerifyRoomChangeHandoverDigest"();
        DROP FUNCTION "BM_CalculateRoomChangeHandoverDigest"(
          "BuildingManagementRoomChangeHandoverEvidence");
        """);
}
