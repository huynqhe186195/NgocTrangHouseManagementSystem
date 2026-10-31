using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2G.5: immutable room handover ledger, plus a deferred same-transaction
/// proof that approved closeout committed Contract.End, completed request,
/// five verified tasks (existing trigger), AND correct final Room state.
/// Never auto-cancels a future tenant's reservation.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029228000_Phase2G5AtomicRoomRelease")]
public sealed class Phase2G5AtomicRoomRelease : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementMoveOutRoomDispositions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ContractId" uuid NOT NULL
                  REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
                "MoveOutRequestId" uuid NOT NULL
                  REFERENCES "BuildingManagementMoveOutRequests" ("Id") ON DELETE RESTRICT,
                "RoomId" uuid NOT NULL
                  REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "CloseoutApprovalId" uuid NOT NULL
                  REFERENCES "BuildingManagementContractDepositCloseoutApprovals" ("Id")
                  ON DELETE RESTRICT,
                "CompletedByUserId" uuid NOT NULL,
                "CompletedAtUtc" timestamptz NOT NULL,
                "ResultRoomStatus" integer NOT NULL,
                "FutureReservationId" uuid NULL
                  REFERENCES "BuildingManagementRoomReservations" ("Id") ON DELETE RESTRICT,
                "FutureSignedContractId" uuid NULL
                  REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
                "RequiresMaintenance" boolean NOT NULL,
                "MaintenanceReason" varchar(500) NULL,
                "MaintenanceEvidenceReference" varchar(500) NULL,
                "HandoverEvidenceReference" varchar(500) NOT NULL,
                "DispositionJson" text NOT NULL,
                "DispositionSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_MoveOutDisposition_Valid" CHECK (
                    "ResultRoomStatus" IN (1,3,4)
                    AND (("ResultRoomStatus" = 3) =
                         ("FutureReservationId" IS NOT NULL OR
                          "FutureSignedContractId" IS NOT NULL))
                    AND NOT ("FutureReservationId" IS NOT NULL AND
                             "FutureSignedContractId" IS NOT NULL)
                    AND ("RequiresMaintenance" = ("ResultRoomStatus" = 4))
                    AND (
                      ("RequiresMaintenance" AND
                       length(btrim("MaintenanceReason")) BETWEEN 8 AND 500 AND
                       length(btrim("MaintenanceEvidenceReference")) BETWEEN 8 AND 500)
                      OR
                      (NOT "RequiresMaintenance" AND
                       "MaintenanceReason" IS NULL AND
                       "MaintenanceEvidenceReference" IS NULL))
                    AND length(btrim("HandoverEvidenceReference")) BETWEEN 8 AND 500
                    AND "DispositionSha256" ~ '^[0-9a-f]{64}$'
                    AND length("DispositionJson") BETWEEN 10 AND 1000000
                    AND jsonb_typeof("DispositionJson"::jsonb) = 'object'
                    AND "CompletedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                )
            );
            CREATE UNIQUE INDEX "UX_BM_MoveOutDisposition_Contract"
                ON "BuildingManagementMoveOutRoomDispositions" ("ContractId");
            CREATE UNIQUE INDEX "UX_BM_MoveOutDisposition_Request"
                ON "BuildingManagementMoveOutRoomDispositions" ("MoveOutRequestId");
            CREATE UNIQUE INDEX "UX_BM_MoveOutDisposition_Approval"
                ON "BuildingManagementMoveOutRoomDispositions" ("CloseoutApprovalId");

            CREATE FUNCTION "BM_ValidateMoveOutRoomDisposition"()
            RETURNS trigger LANGUAGE plpgsql AS $handover_guard$
            DECLARE a record;
            DECLARE r record;
            DECLARE active_reservations bigint;
            DECLARE signed_contracts bigint;
            DECLARE reserved_id uuid;
            DECLARE signed_id uuid;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Signed room handover history is immutable'
                      USING ERRCODE = '23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Completed room handover requires SERIALIZABLE transaction'
                      USING ERRCODE = '23514';
                END IF;
                SELECT * INTO a
                  FROM "BuildingManagementContractDepositCloseoutApprovals"
                 WHERE "Id" = NEW."CloseoutApprovalId" FOR SHARE;
                SELECT * INTO r FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = NEW."MoveOutRequestId" FOR SHARE;
                IF a."Id" IS NULL OR r."Id" IS NULL
                   OR a."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR a."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId"
                   OR a."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR r."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR r."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR NEW."CompletedAtUtc" < a."ApprovedAtUtc"
                   OR NEW."HandoverEvidenceReference" IS DISTINCT FROM
                        a."HandoverEvidenceReference"
                   OR a."ApprovedForfeitAmount" > 0 AND NOT NEW."RequiresMaintenance"
                THEN
                    RAISE EXCEPTION 'Room disposition must match signed closeout, room and damage'
                      USING ERRCODE = '23514';
                END IF;

                SELECT count(*),min("Id") INTO active_reservations,reserved_id
                  FROM "BuildingManagementRoomReservations"
                 WHERE "RoomId" = NEW."RoomId" AND "IsDeleted" = FALSE
                   AND "Status" = 2;
                SELECT count(*),min("Id") INTO signed_contracts,signed_id
                  FROM "BuildingManagementContracts"
                 WHERE "RoomId" = NEW."RoomId" AND "IsDeleted" = FALSE
                   AND "Id" <> NEW."ContractId" AND "Status" = 5;
                IF active_reservations + signed_contracts > 1
                   OR (NEW."RequiresMaintenance" AND
                       active_reservations + signed_contracts > 0)
                   OR NEW."FutureReservationId" IS DISTINCT FROM reserved_id
                   OR NEW."FutureSignedContractId" IS DISTINCT FROM signed_id
                THEN
                    RAISE EXCEPTION 'Room handover conflicts with a future tenant commitment'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $handover_guard$;
            CREATE TRIGGER "TR_BM_MoveOutRoomDisposition_Immutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementMoveOutRoomDispositions"
              FOR EACH ROW EXECUTE FUNCTION "BM_ValidateMoveOutRoomDisposition"();

            CREATE FUNCTION "BM_NoTruncateMoveOutRoomDisposition"()
            RETURNS trigger LANGUAGE plpgsql AS $no_truncate$
            BEGIN
                RAISE EXCEPTION 'Signed room disposition may not be truncated'
                  USING ERRCODE = '23514';
            END;
            $no_truncate$;
            CREATE TRIGGER "TR_BM_MoveOutRoomDisposition_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementMoveOutRoomDispositions"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateMoveOutRoomDisposition"();

            CREATE FUNCTION "BM_RequireRoomReleaseAtMoveOutCommit"()
            RETURNS trigger LANGUAGE plpgsql AS $handover_commit$
            DECLARE d record;
            DECLARE c record;
            DECLARE r record;
            DECLARE actual_room_status integer;
            DECLARE future_reserved bigint;
            DECLARE future_signed bigint;
            BEGIN
                SELECT * INTO d FROM "BuildingManagementMoveOutRoomDispositions"
                 WHERE "CloseoutApprovalId" = NEW."Id";
                SELECT * INTO c FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."ContractId";
                SELECT * INTO r FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = NEW."MoveOutRequestId";
                SELECT "Status" INTO actual_room_status
                  FROM "BuildingManagementRooms"
                 WHERE "Id" = d."RoomId";
                IF d."Id" IS NULL OR c."Id" IS NULL OR r."Id" IS NULL
                   OR d."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR d."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId"
                   OR d."RoomId" IS DISTINCT FROM c."RoomId"
                   OR d."RoomId" IS DISTINCT FROM r."RoomId"
                   OR d."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR d."ResultRoomStatus" IS DISTINCT FROM actual_room_status
                   OR c."Status" IS DISTINCT FROM 3
                   OR c."ActualEndedAt" IS DISTINCT FROM d."CompletedAtUtc"
                   OR r."Status" IS DISTINCT FROM 4
                   OR r."CompletedAt" IS DISTINCT FROM d."CompletedAtUtc"
                   OR d."CompletedAtUtc" < NEW."ApprovedAtUtc"
                   OR NOT EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositSettlementDecisions" sd
                         WHERE sd."ContractId" = NEW."ContractId"
                           AND sd."MoveOutRequestId" = NEW."MoveOutRequestId")
                THEN
                    RAISE EXCEPTION
                      'Atomic move-out must end contract, request and release room with signed disposition'
                      USING ERRCODE = '23514';
                END IF;
                SELECT count(*) INTO future_reserved
                  FROM "BuildingManagementRoomReservations"
                 WHERE "RoomId" = d."RoomId" AND "IsDeleted" = FALSE
                   AND "Status" = 2;
                SELECT count(*) INTO future_signed
                  FROM "BuildingManagementContracts"
                 WHERE "RoomId" = d."RoomId" AND "IsDeleted" = FALSE
                   AND "Id" <> NEW."ContractId" AND "Status" = 5;
                IF future_reserved + future_signed > 1 OR
                   (d."ResultRoomStatus" = 3 AND
                    future_reserved + future_signed <> 1) OR
                   (d."ResultRoomStatus" = 1 AND
                    future_reserved + future_signed <> 0) OR
                   (d."ResultRoomStatus" = 4 AND
                    (future_reserved + future_signed <> 0 OR
                     d."RequiresMaintenance" IS DISTINCT FROM TRUE)) OR
                   (NEW."ApprovedForfeitAmount" > 0 AND
                    d."ResultRoomStatus" <> 4)
                THEN
                    RAISE EXCEPTION 'Committed room state differs from reserved or repair evidence'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END;
            $handover_commit$;
            CREATE CONSTRAINT TRIGGER "TR_BM_Closeout_RequireRoomReleaseAtCommit"
              AFTER INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION "BM_RequireRoomReleaseAtMoveOutCommit"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMoveOutRoomDispositions",
                       "BuildingManagementContractDepositCloseoutApprovals"
              IN ACCESS EXCLUSIVE MODE;
            DO $disposition_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementMoveOutRoomDispositions" LIMIT 1
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Committed move-out exists; refuse atomic handover rollback'
                      USING ERRCODE = '23514';
                END IF;
            END;
            $disposition_down$;
            DROP TRIGGER "TR_BM_Closeout_RequireRoomReleaseAtCommit"
              ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP FUNCTION "BM_RequireRoomReleaseAtMoveOutCommit"();
            DROP TRIGGER "TR_BM_MoveOutRoomDisposition_NoTruncate"
              ON "BuildingManagementMoveOutRoomDispositions";
            DROP FUNCTION "BM_NoTruncateMoveOutRoomDisposition"();
            DROP TRIGGER "TR_BM_MoveOutRoomDisposition_Immutable"
              ON "BuildingManagementMoveOutRoomDispositions";
            DROP FUNCTION "BM_ValidateMoveOutRoomDisposition"();
            DROP TABLE "BuildingManagementMoveOutRoomDispositions";
            """);
    }
}
