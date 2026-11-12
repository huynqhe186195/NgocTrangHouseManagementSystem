using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// An additive Phase 2H repair: 2G.5's immediate Contract.End guard allows
/// only an approved move-out. A valid transfer also ends its source contract,
/// but must carry a DIFFERENT immutable atomic completion and cannot bypass
/// the 2G.5 rules for ordinary move-out. The transfer exception is paired
/// with a deferred same-transaction proof, not a general End authorization.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029235000_Phase2HReconcileContractEndCommitGuards")]
public sealed class Phase2HReconcileContractEndCommitGuards : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Preserve the original move-out guard for every non-transfer
            -- End reason. A RoomChanged transition is only provisionally
            -- permitted; the deferred trigger below requires the exact
            -- authenticated/signed 2H completion in the SAME transaction.
            CREATE OR REPLACE FUNCTION "BM_GuardDirectUnapprovedMoveOutEnd"()
            RETURNS trigger LANGUAGE plpgsql AS $no_bypass$
            BEGIN
                IF TG_TABLE_NAME = 'BuildingManagementContracts' THEN
                    IF OLD."Status" <> 3 AND NEW."Status" = 3 THEN
                        IF OLD."Status" <> 2 OR NEW."ActualEndedAt" IS NULL OR
                           (NEW."EndReason" IS DISTINCT FROM 4 AND NOT EXISTS (
                               SELECT 1
                                 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                                WHERE a."ContractId" = OLD."Id"
                           ))
                        THEN
                            RAISE EXCEPTION
                              'Direct Contract.End bypasses signed CompleteMoveOut or RoomChange'
                              USING ERRCODE = '23514';
                        END IF;
                    END IF;
                ELSIF TG_TABLE_NAME = 'BuildingManagementMoveOutRequests' THEN
                    IF OLD."Status" <> 4 AND NEW."Status" = 4 AND
                       (OLD."Status" <> 1 OR NEW."CompletedAt" IS NULL OR
                        NOT EXISTS (
                           SELECT 1
                             FROM "BuildingManagementContractDepositCloseoutApprovals" a
                            WHERE a."MoveOutRequestId" = OLD."Id"
                              AND a."ContractId" = NEW."ContractId"
                        )) THEN
                        RAISE EXCEPTION
                          'Direct request completion bypasses signed CompleteMoveOut'
                          USING ERRCODE = '23514';
                    END IF;
                ELSE
                    RAISE EXCEPTION 'Unknown move-out end bypass target'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $no_bypass$;

            CREATE FUNCTION "BM_RequireAtomicRoomChangeAtContractEnd"()
            RETURNS trigger LANGUAGE plpgsql AS $transfer_end$
            BEGIN
                IF OLD."Status" = 2 AND NEW."Status" = 3 AND
                   NEW."EndReason" = 4 AND
                   NOT EXISTS (
                       SELECT 1
                         FROM "BuildingManagementRoomChangeCompletions" c
                         JOIN "BuildingManagementRoomChangeRequests" r
                           ON r."Id" = c."RequestId"
                        WHERE c."SourceContractId" = NEW."Id"
                          AND c."SourceRoomId" = OLD."RoomId"
                          AND c."SourceContractId" = r."SourceContractId"
                          AND c."SourceRoomId" = r."SourceRoomId"
                          AND c."TargetRoomId" = r."TargetRoomId"
                          AND c."TargetContractId" <> NEW."Id"
                          AND r."Status" = 4
                          AND r."ReviewedByUserId" = c."CompletedByUserId"
                          AND r."ReviewedAtUtc" = c."CompletedAtUtc"
                   )
                THEN
                    RAISE EXCEPTION
                      'RoomChanged Contract.End requires same-transaction verified atomic transfer'
                      USING ERRCODE = '23514';
                END IF;
                -- The independent deferred 2H completion gate checks the
                -- immutable SHA, invoices, original meters, tenant roster,
                -- source/target room states and exactly one completed carry.
                RETURN NULL;
            END;
            $transfer_end$;

            CREATE CONSTRAINT TRIGGER "TR_BM_RoomChangeContractEnd_CommitGate"
              AFTER UPDATE ON "BuildingManagementContracts"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION "BM_RequireAtomicRoomChangeAtContractEnd"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContracts",
                       "BuildingManagementRoomChangeCompletions"
              IN ACCESS EXCLUSIVE MODE;
            DO $rollback$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementRoomChangeCompletions" LIMIT 1
                ) THEN
                    RAISE EXCEPTION
                      'Completed transfer exists; refuse to restore incompatible move-out-only End gate'
                      USING ERRCODE = '23514';
                END IF;
            END;
            $rollback$;
            DROP TRIGGER "TR_BM_RoomChangeContractEnd_CommitGate"
              ON "BuildingManagementContracts";
            DROP FUNCTION "BM_RequireAtomicRoomChangeAtContractEnd"();

            -- Restore Phase 2G.5's exact original fail-closed rule.
            CREATE OR REPLACE FUNCTION "BM_GuardDirectUnapprovedMoveOutEnd"()
            RETURNS trigger LANGUAGE plpgsql AS $no_bypass$
            BEGIN
                IF TG_TABLE_NAME = 'BuildingManagementContracts' THEN
                    IF OLD."Status" <> 3 AND NEW."Status" = 3 AND
                       (OLD."Status" <> 2 OR NEW."ActualEndedAt" IS NULL OR
                        NOT EXISTS (
                           SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                            WHERE a."ContractId" = OLD."Id"
                        )) THEN
                        RAISE EXCEPTION
                          'Direct Contract.End bypasses signed CompleteMoveOut'
                          USING ERRCODE = '23514';
                    END IF;
                ELSIF TG_TABLE_NAME = 'BuildingManagementMoveOutRequests' THEN
                    IF OLD."Status" <> 4 AND NEW."Status" = 4 AND
                       (OLD."Status" <> 1 OR NEW."CompletedAt" IS NULL OR
                        NOT EXISTS (
                           SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                            WHERE a."MoveOutRequestId" = OLD."Id"
                              AND a."ContractId" = NEW."ContractId"
                        )) THEN
                        RAISE EXCEPTION
                          'Direct request completion bypasses signed CompleteMoveOut'
                          USING ERRCODE = '23514';
                    END IF;
                ELSE
                    RAISE EXCEPTION 'Unknown move-out end bypass target'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $no_bypass$;
            """);
    }
}
