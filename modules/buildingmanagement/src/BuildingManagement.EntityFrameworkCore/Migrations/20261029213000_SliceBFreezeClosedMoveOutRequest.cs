using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only. A signed final handover must never point at a rewritten
/// move-out request. Permit the existing closeout UoW to finish the request
/// after inserting approval; freeze the completed history thereafter.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029213000_SliceBFreezeClosedMoveOutRequest")]
public sealed class SliceBFreezeClosedMoveOutRequest : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_GuardClosedMoveOutSource"()
            RETURNS trigger LANGUAGE plpgsql AS $request_guard$
            DECLARE approved_at timestamptz;
            BEGIN
                -- The final closeout writer locks MoveOutRequest FOR UPDATE
                -- before inserting its approval, so concurrent updates
                -- serialize against that same row until its commit.
                SELECT "ApprovedAtUtc" INTO approved_at
                  FROM "BuildingManagementContractDepositCloseoutApprovals"
                 WHERE "MoveOutRequestId" = OLD."Id";
                IF NOT FOUND THEN
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END IF;
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Signed move-out request cannot be deleted'
                        USING ERRCODE = '23514';
                END IF;

                IF NEW."Id" IS DISTINCT FROM OLD."Id"
                   OR NEW."ContractId" IS DISTINCT FROM OLD."ContractId"
                   OR NEW."RoomId" IS DISTINCT FROM OLD."RoomId"
                   OR NEW."RequestedMoveOutDate" IS DISTINCT FROM OLD."RequestedMoveOutDate"
                   OR NEW."ApprovedMoveOutDate" IS DISTINCT FROM OLD."ApprovedMoveOutDate"
                   OR NEW."Reason" IS DISTINCT FROM OLD."Reason"
                   OR NEW."ReviewNotes" IS DISTINCT FROM OLD."ReviewNotes"
                   OR NEW."ReviewedAt" IS DISTINCT FROM OLD."ReviewedAt"
                   OR NEW."CancelledAt" IS DISTINCT FROM OLD."CancelledAt"
                   OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted"
                THEN
                    RAISE EXCEPTION 'Signed handover freezes original move-out request'
                        USING ERRCODE = '23514';
                END IF;

                -- ABP auditing may flush only ConcurrencyStamp and
                -- modification time while the request is still Approved.
                -- A deferred approval constraint requires Completed at COMMIT.
                IF OLD."Status" = 1 AND NEW."Status" = 1
                   AND OLD."CompletedAt" IS NULL AND NEW."CompletedAt" IS NULL
                THEN
                    RETURN NEW;
                END IF;

                -- The actual application writes approval before Request.Complete.
                -- Only the Approved(1) -> Completed(4) transition and later
                -- audit metadata updates are legal once approval exists.
                IF NEW."Status" <> 4
                   OR NEW."CompletedAt" IS NULL
                   OR NEW."CompletedAt" < approved_at
                   OR (OLD."Status" = 4 AND
                       NEW."CompletedAt" IS DISTINCT FROM OLD."CompletedAt")
                   OR OLD."Status" NOT IN (1, 4)
                THEN
                    RAISE EXCEPTION 'Signed handover permits only request completion and audit-only updates'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $request_guard$;

            CREATE TRIGGER "TR_BM_ClosedMoveOut_FreezeSource"
                BEFORE UPDATE OR DELETE ON "BuildingManagementMoveOutRequests"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardClosedMoveOutSource"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMoveOutRequests",
                       "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $request_guard_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed handover exists; refuse move-out freeze downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $request_guard_down$;
            DROP TRIGGER "TR_BM_ClosedMoveOut_FreezeSource"
                ON "BuildingManagementMoveOutRequests";
            DROP FUNCTION "BM_GuardClosedMoveOutSource"();
            """);
    }
}
