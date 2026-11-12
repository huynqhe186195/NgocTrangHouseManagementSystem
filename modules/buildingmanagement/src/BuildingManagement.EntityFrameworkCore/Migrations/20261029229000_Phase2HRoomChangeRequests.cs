using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2H intake-only and independent reviewer history. This schema
/// intentionally has no status or ledger for an executed room transfer.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029229000_Phase2HRoomChangeRequests")]
public sealed class Phase2HRoomChangeRequests : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeRequests" (
                "Id" uuid PRIMARY KEY,
                "SourceContractId" uuid NOT NULL
                    REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
                "SourceRoomId" uuid NOT NULL
                    REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT,
                "TargetRoomId" uuid NOT NULL
                    REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "RequestedDate" date NOT NULL,
                "Reason" varchar(500) NOT NULL,
                "Status" integer NOT NULL,
                "RequestedByUserId" uuid NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "ReviewedByUserId" uuid NULL,
                "ReviewedAtUtc" timestamptz NULL,
                "ReviewNotes" varchar(500) NULL,
                CONSTRAINT "CK_BM_RoomChangeRequest_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "SourceRoomId" <> "TargetRoomId"
                    AND "Status" IN (0,1,2,3)
                    AND "RequestedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND length(btrim("Reason")) BETWEEN 8 AND 500
                    AND (
                        ("Status" = 0 AND "ReviewedAtUtc" IS NULL
                          AND "ReviewedByUserId" IS NULL AND "ReviewNotes" IS NULL)
                        OR
                        ("Status" IN (1,2,3)
                          AND "ReviewedByUserId" IS NOT NULL
                          AND "ReviewedAtUtc" >= "CreatedAtUtc"
                          AND length(btrim("ReviewNotes")) BETWEEN 8 AND 500)
                    )
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangeRequest_OneOpenPerContract"
              ON "BuildingManagementRoomChangeRequests" ("SourceContractId")
              WHERE "Status" IN (0,1);
            CREATE INDEX "IX_BM_RoomChangeRequest_TargetDate"
              ON "BuildingManagementRoomChangeRequests"
                 ("BuildingId", "TargetRoomId", "RequestedDate");

            CREATE FUNCTION "BM_GuardRoomChangeRequest"()
            RETURNS trigger LANGUAGE plpgsql AS $room_change$
            DECLARE source_contract record;
            DECLARE source_building uuid;
            DECLARE target_building uuid;
            DECLARE source_status integer;
            DECLARE target_status integer;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Room change review history cannot be deleted'
                      USING ERRCODE = '23514';
                ELSIF TG_OP = 'UPDATE' THEN
                    IF OLD."Status" IN (2,3)
                       OR NEW."Id" IS DISTINCT FROM OLD."Id"
                       OR NEW."SourceContractId" IS DISTINCT FROM OLD."SourceContractId"
                       OR NEW."SourceRoomId" IS DISTINCT FROM OLD."SourceRoomId"
                       OR NEW."TargetRoomId" IS DISTINCT FROM OLD."TargetRoomId"
                       OR NEW."BuildingId" IS DISTINCT FROM OLD."BuildingId"
                       OR NEW."RequestedDate" IS DISTINCT FROM OLD."RequestedDate"
                       OR NEW."Reason" IS DISTINCT FROM OLD."Reason"
                       OR NEW."RequestedByUserId" IS DISTINCT FROM OLD."RequestedByUserId"
                       OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                       OR NEW."Status" NOT IN (1,2,3)
                       OR (OLD."Status" = 1 AND NEW."Status" NOT IN (2,3))
                       OR (OLD."Status" = 1 AND NEW."Status" = 1)
                       OR NEW."ReviewedAtUtc" < OLD."ReviewedAtUtc" THEN
                        RAISE EXCEPTION 'Cannot rewrite or advance unauthorized room change'
                          USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                -- Intake is NOT approval. Snapshot the current source and
                -- target, including same-building invariants, under locks.
                IF NEW."Status" <> 0 THEN
                    RAISE EXCEPTION 'New room change must start Pending'
                      USING ERRCODE = '23514';
                END IF;
                SELECT "RoomId","Status","IsDeleted" INTO source_contract
                  FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."SourceContractId" FOR SHARE;
                SELECT r."Status", f."BuildingId"
                  INTO source_status,source_building
                  FROM "BuildingManagementRooms" r
                  JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                 WHERE r."Id" = NEW."SourceRoomId"
                   AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                 FOR SHARE OF r, f;
                SELECT r."Status", f."BuildingId"
                  INTO target_status,target_building
                  FROM "BuildingManagementRooms" r
                  JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                 WHERE r."Id" = NEW."TargetRoomId"
                   AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                 FOR SHARE OF r, f;
                IF source_contract."RoomId" IS DISTINCT FROM NEW."SourceRoomId"
                   OR source_contract."Status" IS DISTINCT FROM 2
                   OR source_contract."IsDeleted" IS DISTINCT FROM FALSE
                   OR source_status IS DISTINCT FROM 2
                   OR target_status IS DISTINCT FROM 1
                   OR source_building IS DISTINCT FROM NEW."BuildingId"
                   OR target_building IS DISTINCT FROM NEW."BuildingId"
                   OR EXISTS (
                      SELECT 1 FROM "BuildingManagementMoveOutRequests" m
                       WHERE m."ContractId" = NEW."SourceContractId"
                         AND m."IsDeleted" = FALSE AND m."Status" IN (0,1))
                   OR EXISTS (
                      SELECT 1 FROM "BuildingManagementContracts" c
                       WHERE c."RoomId" = NEW."TargetRoomId"
                         AND c."IsDeleted" = FALSE AND c."Status" IN (2,5))
                   OR EXISTS (
                      SELECT 1 FROM "BuildingManagementRoomReservations" rr
                       WHERE rr."RoomId" = NEW."TargetRoomId"
                         AND rr."IsDeleted" = FALSE AND rr."Status" IN (1,2))
                THEN
                    RAISE EXCEPTION 'Room change source or target is not eligible'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $room_change$;
            CREATE TRIGGER "TR_BM_RoomChangeRequest_Guard"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeRequests"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeRequest"();

            CREATE FUNCTION "BM_NoTruncateRoomChangeRequest"()
            RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                RAISE EXCEPTION 'Room change audit history is retained'
                  USING ERRCODE = '23514';
            END;
            $hold$;
            CREATE TRIGGER "TR_BM_RoomChangeRequest_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeRequests"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangeRequest"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeRequests"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
              IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeRequests" LIMIT 1) THEN
                RAISE EXCEPTION 'Refuse rollback with recorded room change history'
                  USING ERRCODE = '23514';
              END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangeRequest_NoTruncate"
              ON "BuildingManagementRoomChangeRequests";
            DROP FUNCTION "BM_NoTruncateRoomChangeRequest"();
            DROP TRIGGER "TR_BM_RoomChangeRequest_Guard"
              ON "BuildingManagementRoomChangeRequests";
            DROP FUNCTION "BM_GuardRoomChangeRequest"();
            DROP TABLE "BuildingManagementRoomChangeRequests";
            """);
    }
}
