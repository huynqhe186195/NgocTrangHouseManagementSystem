using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Original signed E/W handover references, never a financial settlement.
/// Attestation sources are frozen until an explicit separate audit workflow.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029233000_Phase2HPhysicalHandoverEvidence")]
public sealed class Phase2HPhysicalHandoverEvidence : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeHandoverEvidence" (
                "Id" uuid PRIMARY KEY,
                "RequestId" uuid NOT NULL
                  REFERENCES "BuildingManagementRoomChangeRequests" ("Id") ON DELETE RESTRICT,
                "SourceContractId" uuid NOT NULL
                  REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
                "SourceRoomId" uuid NOT NULL
                  REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "HandoverDate" date NOT NULL,
                "ElectricityMeterId" uuid NOT NULL
                  REFERENCES "BuildingManagementUtilityMeters" ("Id") ON DELETE RESTRICT,
                "ElectricityReadingId" uuid NOT NULL
                  REFERENCES "BuildingManagementMeterReadings" ("Id") ON DELETE RESTRICT,
                "ElectricityFinalReading" numeric(18,3) NOT NULL,
                "WaterMeterId" uuid NOT NULL
                  REFERENCES "BuildingManagementUtilityMeters" ("Id") ON DELETE RESTRICT,
                "WaterReadingId" uuid NOT NULL
                  REFERENCES "BuildingManagementMeterReadings" ("Id") ON DELETE RESTRICT,
                "WaterFinalReading" numeric(18,3) NOT NULL,
                "InspectionEvidenceReference" varchar(500) NOT NULL,
                "SignedHandoverReference" varchar(500) NOT NULL,
                "AttestedByUserId" uuid NOT NULL,
                "AttestedAtUtc" timestamptz NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_RoomChangeHandover_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "AttestedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ElectricityMeterId" <> "WaterMeterId"
                    AND "ElectricityReadingId" <> "WaterReadingId"
                    AND "ElectricityFinalReading" >= 0 AND "WaterFinalReading" >= 0
                    AND length(btrim("InspectionEvidenceReference")) BETWEEN 12 AND 500
                    AND length(btrim("SignedHandoverReference")) BETWEEN 12 AND 500
                    AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangeHandover_Request"
              ON "BuildingManagementRoomChangeHandoverEvidence" ("RequestId");

            CREATE FUNCTION "BM_GuardRoomChangeHandoverEvidence"()
            RETURNS trigger LANGUAGE plpgsql AS $handover$
            DECLARE req record;
            DECLARE review record;
            DECLARE er record;
            DECLARE wr record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Signed room handover evidence cannot be rewritten'
                        USING ERRCODE = '23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Room handover requires serializable transaction'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
                  WHERE "Id"=NEW."RequestId" FOR SHARE;
                SELECT r.* INTO review
                  FROM "BuildingManagementRoomChangeBusinessPolicyReviews" r
                  JOIN "BuildingManagementRoomChangeBusinessPolicies" p
                    ON p."Id"=r."PolicyId"
                 WHERE r."RequestId"=NEW."RequestId"
                   AND r."Accepted"=TRUE
                   AND p."RequestId"=NEW."RequestId"
                 FOR SHARE OF r;
                SELECT mr.*,um."RoomId",um."UtilityType",
                       um."InstalledDate",um."IsDeleted" AS meter_deleted
                  INTO er
                  FROM "BuildingManagementMeterReadings" mr
                  JOIN "BuildingManagementUtilityMeters" um
                    ON um."Id"=mr."UtilityMeterId"
                 WHERE mr."Id"=NEW."ElectricityReadingId"
                   AND um."Id"=NEW."ElectricityMeterId"
                 FOR SHARE OF mr,um;
                SELECT mr.*,um."RoomId",um."UtilityType",
                       um."InstalledDate",um."IsDeleted" AS meter_deleted
                  INTO wr
                  FROM "BuildingManagementMeterReadings" mr
                  JOIN "BuildingManagementUtilityMeters" um
                    ON um."Id"=mr."UtilityMeterId"
                 WHERE mr."Id"=NEW."WaterReadingId"
                   AND um."Id"=NEW."WaterMeterId"
                 FOR SHARE OF mr,um;
                IF req."Id" IS NULL OR req."Status" NOT IN (0,1)
                   OR review."Id" IS NULL
                   OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
                   OR req."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
                   OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR req."RequestedDate" IS DISTINCT FROM NEW."HandoverDate"
                   OR er."Id" IS NULL OR wr."Id" IS NULL
                   OR er."RoomId" IS DISTINCT FROM req."SourceRoomId"
                   OR wr."RoomId" IS DISTINCT FROM req."SourceRoomId"
                   OR er."UtilityType" IS DISTINCT FROM 1
                   OR wr."UtilityType" IS DISTINCT FROM 2
                   OR er."IsDeleted" IS DISTINCT FROM FALSE
                   OR wr."IsDeleted" IS DISTINCT FROM FALSE
                   OR er.meter_deleted IS DISTINCT FROM FALSE
                   OR wr.meter_deleted IS DISTINCT FROM FALSE
                   OR er."InstalledDate"::date > NEW."HandoverDate"
                   OR wr."InstalledDate"::date > NEW."HandoverDate"
                   OR er."ReadingDate" IS DISTINCT FROM NEW."HandoverDate"
                   OR wr."ReadingDate" IS DISTINCT FROM NEW."HandoverDate"
                   OR er."BillingYear" IS DISTINCT FROM
                        EXTRACT(year FROM NEW."HandoverDate")::integer
                   OR er."BillingMonth" IS DISTINCT FROM
                        EXTRACT(month FROM NEW."HandoverDate")::integer
                   OR wr."BillingYear" IS DISTINCT FROM
                        EXTRACT(year FROM NEW."HandoverDate")::integer
                   OR wr."BillingMonth" IS DISTINCT FROM
                        EXTRACT(month FROM NEW."HandoverDate")::integer
                   OR er."CurrentReading" IS DISTINCT FROM NEW."ElectricityFinalReading"
                   OR wr."CurrentReading" IS DISTINCT FROM NEW."WaterFinalReading"
                   OR er."CurrentReading" < er."PreviousReading"
                   OR wr."CurrentReading" < wr."PreviousReading"
                   OR NEW."AttestedAtUtc" < req."CreatedAtUtc"
                THEN
                    RAISE EXCEPTION 'Handover not anchored to approved source and original readings'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $handover$;
            CREATE TRIGGER "TR_BM_RoomChangeHandover_Guard"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeHandoverEvidence"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeHandoverEvidence"();

            CREATE FUNCTION "BM_NoTruncateRoomChangeHandover"()
            RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                RAISE EXCEPTION 'Signed room change handover evidence cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $hold$;
            CREATE TRIGGER "TR_BM_RoomChangeHandover_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeHandoverEvidence"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangeHandover"();

            -- Source readings must remain byte-for-byte business identical
            -- after handover. Do not block ABP last-modifier metadata only.
            CREATE FUNCTION "BM_FreezeRoomChangeHandoverReading"()
            RETURNS trigger LANGUAGE plpgsql AS $freeze$
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    IF EXISTS (SELECT 1 FROM
                        "BuildingManagementRoomChangeHandoverEvidence" LIMIT 1) THEN
                        RAISE EXCEPTION 'Signed reading evidence cannot be truncated'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END IF;
                IF TG_OP = 'DELETE' OR
                   (NEW."UtilityMeterId" IS DISTINCT FROM OLD."UtilityMeterId"
                    OR NEW."BillingYear" IS DISTINCT FROM OLD."BillingYear"
                    OR NEW."BillingMonth" IS DISTINCT FROM OLD."BillingMonth"
                    OR NEW."PreviousReading" IS DISTINCT FROM OLD."PreviousReading"
                    OR NEW."CurrentReading" IS DISTINCT FROM OLD."CurrentReading"
                    OR NEW."ReadingDate" IS DISTINCT FROM OLD."ReadingDate"
                    OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted")
                THEN
                    IF EXISTS (SELECT 1 FROM
                        "BuildingManagementRoomChangeHandoverEvidence"
                        WHERE "ElectricityReadingId"=OLD."Id"
                           OR "WaterReadingId"=OLD."Id") THEN
                        RAISE EXCEPTION 'Signed handover physical reading is frozen'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
            END;
            $freeze$;
            CREATE TRIGGER "TR_BM_RoomChangeHandover_FreezeReadings"
              BEFORE UPDATE OR DELETE ON "BuildingManagementMeterReadings"
              FOR EACH ROW EXECUTE FUNCTION "BM_FreezeRoomChangeHandoverReading"();
            CREATE TRIGGER "TR_BM_RoomChangeHandover_NoTruncateReadings"
              BEFORE TRUNCATE ON "BuildingManagementMeterReadings"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_FreezeRoomChangeHandoverReading"();

            CREATE FUNCTION "BM_FreezeRoomChangeHandoverMeter"()
            RETURNS trigger LANGUAGE plpgsql AS $meter$
            BEGIN
                IF TG_OP = 'DELETE' OR
                   (NEW."RoomId" IS DISTINCT FROM OLD."RoomId"
                    OR NEW."UtilityType" IS DISTINCT FROM OLD."UtilityType"
                    OR NEW."InstalledDate" IS DISTINCT FROM OLD."InstalledDate"
                    OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted")
                THEN
                    IF EXISTS (SELECT 1 FROM
                        "BuildingManagementRoomChangeHandoverEvidence"
                        WHERE "ElectricityMeterId"=OLD."Id"
                           OR "WaterMeterId"=OLD."Id") THEN
                        RAISE EXCEPTION 'Signed physical meter identity is frozen'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
            END;
            $meter$;
            CREATE TRIGGER "TR_BM_RoomChangeHandover_FreezeMeters"
              BEFORE UPDATE OR DELETE ON "BuildingManagementUtilityMeters"
              FOR EACH ROW EXECUTE FUNCTION "BM_FreezeRoomChangeHandoverMeter"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeHandoverEvidence",
                       "BuildingManagementMeterReadings",
                       "BuildingManagementUtilityMeters"
                IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementRoomChangeHandoverEvidence" LIMIT 1) THEN
                    RAISE EXCEPTION 'Cannot downgrade signed handover records'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangeHandover_FreezeMeters"
              ON "BuildingManagementUtilityMeters";
            DROP FUNCTION "BM_FreezeRoomChangeHandoverMeter"();
            DROP TRIGGER "TR_BM_RoomChangeHandover_NoTruncateReadings"
              ON "BuildingManagementMeterReadings";
            DROP TRIGGER "TR_BM_RoomChangeHandover_FreezeReadings"
              ON "BuildingManagementMeterReadings";
            DROP FUNCTION "BM_FreezeRoomChangeHandoverReading"();
            DROP TRIGGER "TR_BM_RoomChangeHandover_NoTruncate"
              ON "BuildingManagementRoomChangeHandoverEvidence";
            DROP FUNCTION "BM_NoTruncateRoomChangeHandover"();
            DROP TRIGGER "TR_BM_RoomChangeHandover_Guard"
              ON "BuildingManagementRoomChangeHandoverEvidence";
            DROP FUNCTION "BM_GuardRoomChangeHandoverEvidence"();
            DROP TABLE "BuildingManagementRoomChangeHandoverEvidence";
            """);
    }
}
