using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only: reconcile original deposit receipt evidence at signed
/// closeout and freeze it thereafter. No new bank integration or funds move.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029217000_SliceBFreezeClosedDepositReceiptSources")]
public sealed class SliceBFreezeClosedDepositReceiptSources : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_VerifyCloseoutDepositReceiptSources"()
            RETURNS trigger LANGUAGE plpgsql AS $deposit_receipts_check$
            BEGIN
                -- The existing 00_ closeout trigger holds Contract FOR
                -- UPDATE before reaching this trigger. Lock the payment
                -- source through COMMIT as well as checking it, avoiding
                -- a post-read source change before approval commits.
                PERFORM p."Id" FROM "BuildingManagementPayments" p
                JOIN "BuildingManagementDepositSettlements" s
                  ON s."PaymentId"=p."Id"
                 AND s."Type"=3 AND s."Status"=2
                WHERE s."TargetContractId"=NEW."ContractId"
                ORDER BY p."Id" FOR SHARE OF p;

                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                    LEFT JOIN "BuildingManagementPayments" p
                      ON p."Id"=s."PaymentId"
                    WHERE s."TargetContractId"=NEW."ContractId"
                      AND s."Type"=3 AND s."Status"=2
                      AND (p."Id" IS NULL
                           OR p."Status" IS DISTINCT FROM 2
                           OR p."IsDeleted" IS DISTINCT FROM FALSE
                           OR p."PaidAt" IS NULL
                           OR p."Amount" < s."Amount"
                           OR (p."Purpose" = 2 AND
                               (p."ContractId" IS DISTINCT FROM NEW."ContractId"
                                OR s."SourceRoomReservationId" IS NOT NULL))
                           OR (p."Purpose" = 1 AND
                               (s."SourceRoomReservationId" IS NULL OR
                                p."RoomReservationId" IS DISTINCT FROM
                                    s."SourceRoomReservationId"))
                           OR p."Purpose" NOT IN (1,2))
                ) OR EXISTS (
                    -- A reservation receipt may be partly allocated, but
                    -- multiple allocations may never claim more than cash.
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                    JOIN "BuildingManagementPayments" p
                      ON p."Id"=s."PaymentId"
                    WHERE s."TargetContractId"=NEW."ContractId"
                      AND s."Type"=3 AND s."Status"=2
                    GROUP BY p."Id",p."Amount"
                    HAVING SUM(s."Amount") > p."Amount"
                ) THEN
                    RAISE EXCEPTION 'Signed closeout deposit allocations require intact completed receipt sources'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $deposit_receipts_check$;

            -- Alphabetical order: after 00_ Contract/MoveOut lock and
            -- before the original immutable approval validity trigger.
            CREATE TRIGGER "TR_BM_DepositCloseout_01_CheckDepositSources"
                BEFORE INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
                FOR EACH ROW EXECUTE FUNCTION "BM_VerifyCloseoutDepositReceiptSources"();

            CREATE FUNCTION "BM_FreezeClosedDepositReceiptSource"()
            RETURNS trigger LANGUAGE plpgsql AS $deposit_receipts_freeze$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NEW."Purpose"=2 AND NEW."ContractId" IS NOT NULL THEN
                        PERFORM 1 FROM "BuildingManagementContracts"
                         WHERE "Id"=NEW."ContractId" FOR UPDATE;
                        IF EXISTS (
                            SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                             WHERE "ContractId"=NEW."ContractId"
                        ) THEN
                            RAISE EXCEPTION 'Closed contract cannot accept new deposit receipts'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END IF;

                -- Contract locks serialize source changes with final
                -- approval, including reservation deposits whose payment
                -- has no direct ContractId. Use stable UUID order.
                IF TG_OP='UPDATE' THEN
                    PERFORM c."Id" FROM "BuildingManagementContracts" c
                     WHERE c."Id" IN (
                        SELECT s."TargetContractId"
                        FROM "BuildingManagementDepositSettlements" s
                        WHERE s."PaymentId"=OLD."Id"
                          AND s."Type"=3 AND s."Status"=2
                        UNION
                        SELECT OLD."ContractId" WHERE OLD."Purpose"=2
                        UNION
                        SELECT NEW."ContractId" WHERE NEW."Purpose"=2
                     ) ORDER BY c."Id" FOR UPDATE;
                ELSE
                    PERFORM c."Id" FROM "BuildingManagementContracts" c
                     WHERE c."Id" IN (
                        SELECT s."TargetContractId"
                        FROM "BuildingManagementDepositSettlements" s
                        WHERE s."PaymentId"=OLD."Id"
                          AND s."Type"=3 AND s."Status"=2
                        UNION
                        SELECT OLD."ContractId" WHERE OLD."Purpose"=2
                     ) ORDER BY c."Id" FOR UPDATE;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                    JOIN "BuildingManagementContractDepositCloseoutApprovals" a
                      ON a."ContractId"=s."TargetContractId"
                    WHERE s."PaymentId"=OLD."Id"
                      AND s."Type"=3 AND s."Status"=2
                ) OR (OLD."Purpose"=2 AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                     WHERE a."ContractId"=OLD."ContractId"
                )) OR (TG_OP='UPDATE' AND NEW."Purpose"=2 AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                     WHERE a."ContractId"=NEW."ContractId"
                )) THEN
                    RAISE EXCEPTION 'Signed deposit receipt source cannot be modified'
                        USING ERRCODE = '23514';
                END IF;

                RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
            END;
            $deposit_receipts_freeze$;
            CREATE TRIGGER "TR_BM_ClosedDepositReceipt_SourceFreeze"
                BEFORE INSERT OR UPDATE OR DELETE ON "BuildingManagementPayments"
                FOR EACH ROW EXECUTE FUNCTION "BM_FreezeClosedDepositReceiptSource"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals",
                       "BuildingManagementDepositSettlements",
                       "BuildingManagementPayments"
                IN ACCESS EXCLUSIVE MODE;
            DO $deposit_receipts_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed deposit receipt evidence exists; refuse freeze downgrade'
                        USING ERRCODE='23514';
                END IF;
            END;
            $deposit_receipts_down$;
            DROP TRIGGER "TR_BM_DepositCloseout_01_CheckDepositSources"
              ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP TRIGGER "TR_BM_ClosedDepositReceipt_SourceFreeze"
              ON "BuildingManagementPayments";
            DROP FUNCTION "BM_VerifyCloseoutDepositReceiptSources"();
            DROP FUNCTION "BM_FreezeClosedDepositReceiptSource"();
            """);
    }
}
