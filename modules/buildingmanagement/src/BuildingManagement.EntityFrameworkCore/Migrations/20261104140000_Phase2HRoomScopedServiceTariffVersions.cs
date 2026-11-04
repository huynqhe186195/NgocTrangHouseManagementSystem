using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Append-only room/service pricing history. Version N+1 may supersede an
/// open-ended N from a strictly later effective date; no old row is edited.
/// A finite explicitly signed end date cannot be retroactively overlapped.
/// The first writer must still be authorized by the authenticated Owner API:
/// an SQL constraint is not a replacement for current ABP permissions.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104140000_Phase2HRoomScopedServiceTariffVersions")]
public sealed class Phase2HRoomScopedServiceTariffVersions : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE TABLE "BuildingManagementRoomScopedServiceTariffVersions" (
          "Id" uuid PRIMARY KEY,
          "RoomId" uuid NOT NULL REFERENCES "BuildingManagementRooms"("Id")
            ON DELETE RESTRICT,
          "ServiceId" uuid NOT NULL,
          "Version" integer NOT NULL,
          "UnitPriceVnd" numeric(18,0) NOT NULL,
          "BillingUnit" integer NOT NULL,
          "ChargingRule" integer NOT NULL,
          "EffectiveFrom" date NOT NULL,
          "EffectiveThrough" date NULL,
          "ApprovedByUserId" uuid NOT NULL,
          "ApprovedAtUtc" timestamptz NOT NULL,
          "SignedPolicyReference" varchar(500) NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          CONSTRAINT "CK_BM_RoomScopedServiceTariff_Valid" CHECK (
            "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND "RoomId" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND "ServiceId" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND "ApprovedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND "Version">0
            AND "UnitPriceVnd">=0
            AND "BillingUnit" IN (1,2,3)
            AND "ChargingRule" IN (1,2,3)
            AND (("BillingUnit"=3)=("ChargingRule"=3))
            AND ("EffectiveThrough" IS NULL
                 OR "EffectiveThrough">="EffectiveFrom")
            AND length(btrim("SignedPolicyReference")) BETWEEN 12 AND 500
            AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
          ),
          CONSTRAINT "UX_BM_RoomScopedServiceTariff_Version"
            UNIQUE("RoomId","ServiceId","Version"),
          CONSTRAINT "UX_BM_RoomScopedServiceTariff_Start"
            UNIQUE("RoomId","ServiceId","EffectiveFrom")
        );

        CREATE FUNCTION "BM_2HVerifyRoomScopedServiceTariffAppend"()
        RETURNS trigger LANGUAGE plpgsql AS $room_service$
        DECLARE
          last_version record;
        BEGIN
          -- Serialize competing writers for the exact room by locking
          -- the original room row, not merely checking an optimistic MAX.
          PERFORM 1
            FROM "BuildingManagementRooms"
           WHERE "Id"=NEW."RoomId" AND "IsDeleted"=FALSE
           FOR UPDATE;
          IF NOT FOUND THEN
            RAISE EXCEPTION 'Room-specific tariff requires a live source room'
              USING ERRCODE='23514';
          END IF;

          -- Never introduce a newly approved retroactive price. The
          -- authoritative actor/time is validated by the authenticated API.
          IF NEW."EffectiveFrom" <
             (NEW."ApprovedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date THEN
            RAISE EXCEPTION 'Room service tariff cannot be backdated'
              USING ERRCODE='23514';
          END IF;

          SELECT "Version","EffectiveFrom","EffectiveThrough"
            INTO last_version
            FROM "BuildingManagementRoomScopedServiceTariffVersions"
           WHERE "RoomId"=NEW."RoomId" AND "ServiceId"=NEW."ServiceId"
           ORDER BY "Version" DESC LIMIT 1;
          IF FOUND THEN
             IF NEW."Version" IS DISTINCT FROM last_version."Version"+1
                OR NEW."EffectiveFrom" <= last_version."EffectiveFrom"
                OR (last_version."EffectiveThrough" IS NOT NULL AND
                    NEW."EffectiveFrom"<=last_version."EffectiveThrough") THEN
               RAISE EXCEPTION
                'Service tariff versions must be strictly prospective, sequential and nonoverlapping'
                 USING ERRCODE='23514';
             END IF;
          ELSIF NEW."Version"<>1 THEN
             RAISE EXCEPTION 'First service tariff version must equal one'
               USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $room_service$;

        CREATE TRIGGER "TR_BM_2H_RoomServiceTariff_Append"
          BEFORE INSERT ON "BuildingManagementRoomScopedServiceTariffVersions"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HVerifyRoomScopedServiceTariffAppend"();

        CREATE FUNCTION "BM_2HDenyRoomScopedServiceTariffMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $room_service_immutable$
        BEGIN
          RAISE EXCEPTION
           'Original room-specific service price history is append-only'
           USING ERRCODE='23514';
        END;
        $room_service_immutable$;

        CREATE TRIGGER "TR_BM_2H_RoomServiceTariff_Immutable"
          BEFORE UPDATE OR DELETE ON
           "BuildingManagementRoomScopedServiceTariffVersions"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HDenyRoomScopedServiceTariffMutation"();

        CREATE TRIGGER "TR_BM_2H_RoomServiceTariff_NoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomScopedServiceTariffVersions"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_2HDenyRoomScopedServiceTariffMutation"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomScopedServiceTariffVersions"
          IN ACCESS EXCLUSIVE MODE;
        DO $preserve$
        BEGIN
          IF EXISTS (
              SELECT 1 FROM "BuildingManagementRoomScopedServiceTariffVersions"
              LIMIT 1) THEN
            RAISE EXCEPTION
              'Cannot remove approved room-specific service tariff history'
              USING ERRCODE='23514';
          END IF;
        END;
        $preserve$;
        DROP TABLE "BuildingManagementRoomScopedServiceTariffVersions";
        DROP FUNCTION "BM_2HVerifyRoomScopedServiceTariffAppend"();
        DROP FUNCTION "BM_2HDenyRoomScopedServiceTariffMutation"();
        """);
}
