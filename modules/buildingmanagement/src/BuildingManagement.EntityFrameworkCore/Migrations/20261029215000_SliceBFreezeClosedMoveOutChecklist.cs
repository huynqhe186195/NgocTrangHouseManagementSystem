using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// SOURCE-ONLY: signed move-out checklist cannot be rewritten or bypassed.
/// Non-meter task completion occurs after approval INSERT in the same UoW.
/// A deferred trigger requires all five exact tasks complete at commit.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029215000_SliceBFreezeClosedMoveOutChecklist")]
public sealed class SliceBFreezeClosedMoveOutChecklist : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Fires before the original TR_BM_DepositCloseout_Immutable
            -- trigger (PostgreSQL alphabetical trigger order). Contract then
            -- request lock matches the staging closeout writer's lock order.
            -- Competing checklist writers must lock the same request row.
            CREATE FUNCTION "BM_LockMoveOutChecklistForCloseout"()
            RETURNS trigger LANGUAGE plpgsql AS $closeout_task_lock$
            BEGIN
                PERFORM 1 FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."ContractId" FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'Closeout checklist contract missing'
                        USING ERRCODE = '23514';
                END IF;
                PERFORM 1 FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = NEW."MoveOutRequestId"
                   AND "ContractId" = NEW."ContractId" FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'Closeout checklist request missing or foreign'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $closeout_task_lock$;
            CREATE TRIGGER "TR_BM_00_CloseoutChecklistRequestLock"
                BEFORE INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
                FOR EACH ROW EXECUTE FUNCTION "BM_LockMoveOutChecklistForCloseout"();

            CREATE FUNCTION "BM_GuardClosedMoveOutChecklist"()
            RETURNS trigger LANGUAGE plpgsql AS $checklist_guard$
            DECLARE source_request uuid;
            DECLARE original_approved_at timestamptz;
            DECLARE target_approved_at timestamptz;
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    IF EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                    ) THEN
                        RAISE EXCEPTION 'Signed move-out checklist cannot be truncated'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END IF;

                -- Request row lock serializes checklist edits against
                -- approval INSERT, not just against task-row updates.
                IF TG_OP = 'INSERT' THEN
                    source_request := NEW."MoveOutRequestId";
                    PERFORM 1 FROM "BuildingManagementMoveOutRequests"
                     WHERE "Id" = source_request FOR UPDATE;
                    IF EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                         WHERE "MoveOutRequestId" = source_request
                    ) THEN
                        RAISE EXCEPTION 'Signed checklist cannot accept new tasks'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;

                PERFORM 1 FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = OLD."MoveOutRequestId"
                    OR (TG_OP = 'UPDATE' AND "Id" = NEW."MoveOutRequestId")
                 ORDER BY "Id" FOR UPDATE;
                SELECT "ApprovedAtUtc" INTO original_approved_at
                  FROM "BuildingManagementContractDepositCloseoutApprovals"
                 WHERE "MoveOutRequestId" = OLD."MoveOutRequestId";

                IF TG_OP = 'DELETE' THEN
                    IF original_approved_at IS NOT NULL THEN
                        RAISE EXCEPTION 'Signed checklist task cannot be deleted'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN OLD;
                END IF;

                SELECT "ApprovedAtUtc" INTO target_approved_at
                  FROM "BuildingManagementContractDepositCloseoutApprovals"
                 WHERE "MoveOutRequestId" = NEW."MoveOutRequestId";
                IF original_approved_at IS NULL AND target_approved_at IS NULL THEN
                    RETURN NEW;
                END IF;

                IF NEW."Id" IS DISTINCT FROM OLD."Id"
                   OR NEW."MoveOutRequestId" IS DISTINCT FROM OLD."MoveOutRequestId"
                   OR NEW."Type" IS DISTINCT FROM OLD."Type"
                THEN
                    RAISE EXCEPTION 'Signed checklist task identity is immutable'
                        USING ERRCODE = '23514';
                END IF;

                -- ABP may flush only audit metadata during closeout.
                IF NEW."IsCompleted" IS NOT DISTINCT FROM OLD."IsCompleted"
                   AND NEW."CompletedAt" IS NOT DISTINCT FROM OLD."CompletedAt"
                THEN
                    RETURN NEW;
                END IF;

                -- Only a single incomplete -> completed NON-meter signoff
                -- may follow approval. The meter proof must predate closeout.
                IF original_approved_at IS NOT NULL
                   AND OLD."Type" BETWEEN 1 AND 4
                   AND OLD."IsCompleted" = FALSE
                   AND OLD."CompletedAt" IS NULL
                   AND NEW."IsCompleted" = TRUE
                   AND NEW."CompletedAt" >= original_approved_at
                THEN
                    RETURN NEW;
                END IF;

                RAISE EXCEPTION 'Signed checklist cannot be reopened or re-dated'
                    USING ERRCODE = '23514';
            END;
            $checklist_guard$;
            CREATE TRIGGER "TR_BM_ClosedChecklist_FreezeTask"
                BEFORE INSERT OR UPDATE OR DELETE ON "BuildingManagementMoveOutChecklistTasks"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardClosedMoveOutChecklist"();
            CREATE TRIGGER "TR_BM_ClosedChecklist_NoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementMoveOutChecklistTasks"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardClosedMoveOutChecklist"();

            CREATE FUNCTION "BM_RequireCompletedCloseoutChecklist"()
            RETURNS trigger LANGUAGE plpgsql AS $checklist_commit$
            DECLARE total_rows bigint;
            DECLARE unique_types bigint;
            DECLARE valid_rows bigint;
            BEGIN
                SELECT count(*), count(DISTINCT "Type"),
                       count(*) FILTER (
                           WHERE "Type" BETWEEN 0 AND 4
                             AND "IsCompleted" = TRUE
                             AND "CompletedAt" IS NOT NULL
                             AND ("Type" = 0 OR "CompletedAt" >= NEW."ApprovedAtUtc")
                       )
                  INTO total_rows, unique_types, valid_rows
                  FROM "BuildingManagementMoveOutChecklistTasks"
                 WHERE "MoveOutRequestId" = NEW."MoveOutRequestId";
                IF total_rows <> 5 OR unique_types <> 5 OR valid_rows <> 5 THEN
                    RAISE EXCEPTION 'Closeout must commit all five uniquely completed checklist tasks'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END;
            $checklist_commit$;
            CREATE CONSTRAINT TRIGGER "TR_BM_DepositCloseout_RequireChecklistAtCommit"
                AFTER INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION "BM_RequireCompletedCloseoutChecklist"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals",
                       "BuildingManagementMoveOutChecklistTasks"
                IN ACCESS EXCLUSIVE MODE;
            DO $closed_checklist_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed checklist exists; refuse integrity downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $closed_checklist_down$;
            DROP TRIGGER "TR_BM_DepositCloseout_RequireChecklistAtCommit"
                ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP TRIGGER "TR_BM_00_CloseoutChecklistRequestLock"
                ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP TRIGGER "TR_BM_ClosedChecklist_FreezeTask"
                ON "BuildingManagementMoveOutChecklistTasks";
            DROP TRIGGER "TR_BM_ClosedChecklist_NoTruncate"
                ON "BuildingManagementMoveOutChecklistTasks";
            DROP FUNCTION "BM_RequireCompletedCloseoutChecklist"();
            DROP FUNCTION "BM_GuardClosedMoveOutChecklist"();
            DROP FUNCTION "BM_LockMoveOutChecklistForCloseout"();
            """);
    }
}
