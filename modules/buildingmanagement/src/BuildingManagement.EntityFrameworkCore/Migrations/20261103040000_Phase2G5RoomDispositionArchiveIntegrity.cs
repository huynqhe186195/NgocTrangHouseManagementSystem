using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2G.5 additive, fail-closed PostgreSQL archive integrity check.
/// The existing atomic release, approval, reservation and immutability
/// triggers remain untouched. A syntactically valid SHA is not enough:
/// the archived payload and all typed handover fields must agree.
/// Staging migration only; never run against a live application database.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103040000_Phase2G5RoomDispositionArchiveIntegrity")]
public sealed class Phase2G5RoomDispositionArchiveIntegrity : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE FUNCTION "BM_VerifyMoveOutDispositionArchive"()
        RETURNS trigger LANGUAGE plpgsql AS $archive$
        DECLARE archived jsonb;
        BEGIN
          IF TG_OP <> 'INSERT' OR
             current_setting('transaction_isolation') <> 'serializable' THEN
            RAISE EXCEPTION 'Move-out archive requires serializable immutable insert'
              USING ERRCODE = '23514';
          END IF;

          archived := NEW."DispositionJson"::jsonb;
          IF jsonb_typeof(archived) IS DISTINCT FROM 'object' OR
             NOT (archived ?& ARRAY[
               'ContractId','MoveOutRequestId','RoomId','BuildingId',
               'CloseoutApprovalId','CompletedByUserId','CompletedAtUtc',
               'ResultRoomStatus','FutureReservationId','FutureSignedContractId',
               'RequiresMaintenance','MaintenanceReason',
               'MaintenanceEvidenceReference','HandoverEvidenceReference'
             ]) OR
             encode(sha256(convert_to(NEW."DispositionJson", 'UTF8')), 'hex')
               IS DISTINCT FROM NEW."DispositionSha256" OR
             (archived ->> 'ContractId')::uuid
               IS DISTINCT FROM NEW."ContractId" OR
             (archived ->> 'MoveOutRequestId')::uuid
               IS DISTINCT FROM NEW."MoveOutRequestId" OR
             (archived ->> 'RoomId')::uuid
               IS DISTINCT FROM NEW."RoomId" OR
             (archived ->> 'BuildingId')::uuid
               IS DISTINCT FROM NEW."BuildingId" OR
             (archived ->> 'CloseoutApprovalId')::uuid
               IS DISTINCT FROM NEW."CloseoutApprovalId" OR
             (archived ->> 'CompletedByUserId')::uuid
               IS DISTINCT FROM NEW."CompletedByUserId" OR
             (archived ->> 'CompletedAtUtc')::timestamptz
               IS DISTINCT FROM NEW."CompletedAtUtc" OR
             (archived ->> 'ResultRoomStatus')::integer
               IS DISTINCT FROM NEW."ResultRoomStatus" OR
             (archived ->> 'FutureReservationId')::uuid
               IS DISTINCT FROM NEW."FutureReservationId" OR
             (archived ->> 'FutureSignedContractId')::uuid
               IS DISTINCT FROM NEW."FutureSignedContractId" OR
             (archived ->> 'RequiresMaintenance')::boolean
               IS DISTINCT FROM NEW."RequiresMaintenance" OR
             (archived ->> 'MaintenanceReason')
               IS DISTINCT FROM NEW."MaintenanceReason" OR
             (archived ->> 'MaintenanceEvidenceReference')
               IS DISTINCT FROM NEW."MaintenanceEvidenceReference" OR
             (archived ->> 'HandoverEvidenceReference')
               IS DISTINCT FROM NEW."HandoverEvidenceReference"
          THEN
            RAISE EXCEPTION 'Move-out archive digest or typed handover evidence mismatch'
              USING ERRCODE = '23514';
          END IF;
          RETURN NEW;
        END;
        $archive$;

        CREATE TRIGGER "TR_BM_VerifyMoveOutDispositionArchive"
          BEFORE INSERT ON "BuildingManagementMoveOutRoomDispositions"
          FOR EACH ROW EXECUTE FUNCTION "BM_VerifyMoveOutDispositionArchive"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementMoveOutRoomDispositions"
          IN ACCESS EXCLUSIVE MODE;
        DO $rollback$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementMoveOutRoomDispositions" LIMIT 1
          ) THEN
            RAISE EXCEPTION 'Cannot remove move-out archive proof with signed handover history'
              USING ERRCODE = '23514';
          END IF;
        END;
        $rollback$;

        DROP TRIGGER "TR_BM_VerifyMoveOutDispositionArchive"
          ON "BuildingManagementMoveOutRoomDispositions";
        DROP FUNCTION "BM_VerifyMoveOutDispositionArchive"();
        """);
}
