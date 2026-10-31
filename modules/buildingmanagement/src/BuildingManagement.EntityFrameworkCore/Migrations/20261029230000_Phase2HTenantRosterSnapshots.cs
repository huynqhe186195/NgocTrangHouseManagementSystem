using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only append-only party roster. Does not authorize an occupant to
/// move, sign a new contract, transfer a deposit or change room occupancy.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029230000_Phase2HTenantRosterSnapshots")]
public sealed class Phase2HTenantRosterSnapshots : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeTenantRosterSnapshots" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "RoomChangeRequestId" uuid NOT NULL
                    REFERENCES "BuildingManagementRoomChangeRequests" ("Id")
                    ON DELETE RESTRICT,
                "SourceContractId" uuid NOT NULL
                    REFERENCES "BuildingManagementContracts" ("Id")
                    ON DELETE RESTRICT,
                "SourceRoomId" uuid NOT NULL
                    REFERENCES "BuildingManagementRooms" ("Id")
                    ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "CapturedByUserId" uuid NOT NULL,
                "CapturedAtUtc" timestamptz NOT NULL,
                "RosterCount" integer NOT NULL,
                "RosterJson" text NOT NULL,
                "RosterSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_RoomChangeRoster_Canonical" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "CapturedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "RosterCount" BETWEEN 1 AND 100
                    AND length("RosterJson") BETWEEN 40 AND 20000
                    AND jsonb_typeof("RosterJson"::jsonb) = 'object'
                    AND ("RosterJson"::jsonb->>'requestId') = "RoomChangeRequestId"::text
                    AND ("RosterJson"::jsonb->>'contractId') = "SourceContractId"::text
                    AND ("RosterJson"::jsonb->>'sourceRoomId') = "SourceRoomId"::text
                    AND ("RosterJson"::jsonb->>'buildingId') = "BuildingId"::text
                    AND jsonb_typeof("RosterJson"::jsonb->'tenants') = 'array'
                    AND jsonb_array_length("RosterJson"::jsonb->'tenants') = "RosterCount"
                    AND "RosterSha256" = encode(
                        sha256(convert_to("RosterJson",'UTF8')), 'hex')
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangeTenantRoster_Request"
                ON "BuildingManagementRoomChangeTenantRosterSnapshots"
                    ("RoomChangeRequestId");

            CREATE FUNCTION "BM_ValidateRoomChangeRosterSnapshot"()
            RETURNS trigger LANGUAGE plpgsql AS $roster$
            DECLARE req record;
            DECLARE source_contract record;
            DECLARE db_count integer;
            DECLARE verified_count integer;
            DECLARE primary_count integer;
            DECLARE member_count integer;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Captured room-change roster history is immutable'
                        USING ERRCODE = '23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Tenant roster snapshot requires SERIALIZABLE transaction'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
                  WHERE "Id" = NEW."RoomChangeRequestId" FOR SHARE;
                SELECT "RoomId","Status","IsDeleted" INTO source_contract
                  FROM "BuildingManagementContracts"
                  WHERE "Id" = NEW."SourceContractId" FOR SHARE;
                IF req."Id" IS NULL
                   OR req."Status" NOT IN (0,1)
                   OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
                   OR req."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
                   OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR NEW."CapturedAtUtc" < req."CreatedAtUtc"
                   OR source_contract."RoomId" IS DISTINCT FROM NEW."SourceRoomId"
                   OR source_contract."Status" IS DISTINCT FROM 2
                   OR source_contract."IsDeleted" IS DISTINCT FROM FALSE THEN
                    RAISE EXCEPTION 'Roster does not match active open transfer request'
                        USING ERRCODE = '23514';
                END IF;

                SELECT count(*),count(*) FILTER (WHERE "Role" = 1)
                  INTO db_count,primary_count
                  FROM "BuildingManagementContractTenants"
                 WHERE "ContractId" = NEW."SourceContractId" AND "IsDeleted" = FALSE;
                SELECT count(*) INTO verified_count
                  FROM jsonb_array_elements(NEW."RosterJson"::jsonb->'tenants') j
                 WHERE j ? 'tenantId' AND j ? 'role'
                   AND (j->>'role') IN ('1','2')
                   AND (j->>'tenantId') ~* '^[0-9a-f-]{36}$';
                SELECT count(DISTINCT j->>'tenantId') INTO member_count
                  FROM jsonb_array_elements(NEW."RosterJson"::jsonb->'tenants') j;

                IF db_count <> NEW."RosterCount"
                   OR verified_count <> NEW."RosterCount"
                   OR member_count <> NEW."RosterCount"
                   OR primary_count <> 1
                   OR EXISTS (
                      SELECT t."TenantId"::text AS tenant, t."Role" AS role
                        FROM "BuildingManagementContractTenants" t
                       WHERE t."ContractId" = NEW."SourceContractId"
                         AND t."IsDeleted" = FALSE
                      EXCEPT
                      SELECT j->>'tenantId', (j->>'role')::int
                        FROM jsonb_array_elements(
                            NEW."RosterJson"::jsonb->'tenants') j)
                   OR EXISTS (
                      SELECT j->>'tenantId' AS tenant, (j->>'role')::int AS role
                        FROM jsonb_array_elements(
                            NEW."RosterJson"::jsonb->'tenants') j
                      EXCEPT
                      SELECT t."TenantId"::text,t."Role"
                        FROM "BuildingManagementContractTenants" t
                       WHERE t."ContractId" = NEW."SourceContractId"
                         AND t."IsDeleted" = FALSE)
                THEN
                    RAISE EXCEPTION 'Roster must exactly match live contract parties'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $roster$;

            CREATE TRIGGER "TR_BM_RoomChangeRoster_Guard"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeTenantRosterSnapshots"
              FOR EACH ROW EXECUTE FUNCTION "BM_ValidateRoomChangeRosterSnapshot"();

            CREATE FUNCTION "BM_NoTruncateRoomChangeRoster"()
            RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                RAISE EXCEPTION 'Captured roster history cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $hold$;
            CREATE TRIGGER "TR_BM_RoomChangeRoster_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeTenantRosterSnapshots"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangeRoster"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeTenantRosterSnapshots"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementRoomChangeTenantRosterSnapshots" LIMIT 1) THEN
                    RAISE EXCEPTION 'Refuse rollback when tenant roster history exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangeRoster_NoTruncate"
              ON "BuildingManagementRoomChangeTenantRosterSnapshots";
            DROP FUNCTION "BM_NoTruncateRoomChangeRoster"();
            DROP TRIGGER "TR_BM_RoomChangeRoster_Guard"
              ON "BuildingManagementRoomChangeTenantRosterSnapshots";
            DROP FUNCTION "BM_ValidateRoomChangeRosterSnapshot"();
            DROP TABLE "BuildingManagementRoomChangeTenantRosterSnapshots";
            """);
    }
}
