using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2H: additive deferred PostgreSQL proof that a completed source
/// room cannot be released as Available while a confirmed next occupant
/// exists, nor labelled Reserved without exactly one valid commitment.
/// This remains a safe-lane-only commit guard; no financial writer unlocked.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103050000_Phase2HSourceRoomDispositionCommitGuard")]
public sealed class Phase2HSourceRoomDispositionCommitGuard : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE FUNCTION "BM_VerifyRoomChangeSourceDispositionAtCommit"()
        RETURNS trigger LANGUAGE plpgsql AS $source_guard$
        DECLARE actual_status integer;
        DECLARE reserved_count bigint;
        DECLARE pending_count bigint;
        DECLARE signed_count bigint;
        DECLARE other_active_count bigint;
        DECLARE confirmed_count bigint;
        BEGIN
          IF current_setting('transaction_isolation') <> 'serializable' THEN
            RAISE EXCEPTION 'Room change source disposition must commit under SERIALIZABLE'
              USING ERRCODE='23514';
          END IF;

          SELECT "Status" INTO actual_status
            FROM "BuildingManagementRooms"
           WHERE "Id" = NEW."SourceRoomId";

          SELECT count(*) INTO reserved_count
            FROM "BuildingManagementRoomReservations"
           WHERE "RoomId" = NEW."SourceRoomId"
             AND "IsDeleted" = FALSE AND "Status" = 2;
          SELECT count(*) INTO pending_count
            FROM "BuildingManagementRoomReservations"
           WHERE "RoomId" = NEW."SourceRoomId"
             AND "IsDeleted" = FALSE AND "Status" = 1;
          SELECT count(*) INTO signed_count
            FROM "BuildingManagementContracts"
           WHERE "RoomId" = NEW."SourceRoomId"
             AND "Id" <> NEW."SourceContractId"
             AND "IsDeleted" = FALSE AND "Status" = 5;
          SELECT count(*) INTO other_active_count
            FROM "BuildingManagementContracts"
           WHERE "RoomId" = NEW."SourceRoomId"
             AND "Id" <> NEW."SourceContractId"
             AND "IsDeleted" = FALSE AND "Status" = 2;

          confirmed_count := reserved_count + signed_count;

          IF actual_status IS DISTINCT FROM NEW."SourceRoomDisposition"
             OR NEW."SourceRoomDisposition" NOT IN (1,3,4)
             OR pending_count > 0
             OR other_active_count > 0
             OR confirmed_count > 1
             OR (confirmed_count = 1 AND NEW."SourceRoomDisposition" <> 3)
             OR (confirmed_count = 0 AND NEW."SourceRoomDisposition" = 3)
          THEN
            RAISE EXCEPTION
              'Room change source disposition contradicts future occupancy or unresolved reservation'
              USING ERRCODE='23514';
          END IF;
          RETURN NULL;
        END;
        $source_guard$;

        CREATE CONSTRAINT TRIGGER "TR_BM_RoomChangeSourceDisposition_CommitGate"
          AFTER INSERT ON "BuildingManagementRoomChangeCompletions"
          DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRoomChangeSourceDispositionAtCommit"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeCompletions"
          IN ACCESS EXCLUSIVE MODE;
        DO $rollback$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeCompletions" LIMIT 1
          ) THEN
            RAISE EXCEPTION
              'Refuse to discard source room future-commitment gate with completed transfers'
              USING ERRCODE='23514';
          END IF;
        END;
        $rollback$;

        DROP TRIGGER "TR_BM_RoomChangeSourceDisposition_CommitGate"
          ON "BuildingManagementRoomChangeCompletions";
        DROP FUNCTION "BM_VerifyRoomChangeSourceDispositionAtCommit"();
        """);
}
