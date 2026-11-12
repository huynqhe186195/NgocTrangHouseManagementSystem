using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// OI-04: signed debt review and Owner approval MUST be backed by completed,
/// uniquely allocated real deposit receipts, not merely Contract.DepositAmount.
/// This is a source-provenance safety gate; it NEVER spends cash or debt.
/// Applied only to scratch PostgreSQL during current development.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101022000_Phase2G4RequireCashBackedDebtApproval")]
public sealed class Phase2G4RequireCashBackedDebtApproval : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_GuardOI04CashBackedApproval"()
            RETURNS trigger LANGUAGE plpgsql AS $cash$
            DECLARE incoming numeric;
            BEGIN
              IF TG_OP <> 'INSERT' OR
                 current_setting('transaction_isolation') <> 'serializable'
              THEN
                RAISE EXCEPTION 'Debt approval source check requires serializable insert'
                  USING ERRCODE='23514';
              END IF;

              -- The Contract is also locked by the existing independent
              -- review/Owner SQL guards; preserve the common lock order.
              PERFORM "Id" FROM "BuildingManagementContracts"
                WHERE "Id"=NEW."ContractId" FOR UPDATE;
              IF NOT FOUND THEN
                RAISE EXCEPTION 'Contract not found for cash-backed review'
                  USING ERRCODE='23514';
              END IF;

              -- Freeze existing receipt and allocation rows through the
              -- transaction. Serializable detects competing phantom inserts.
              PERFORM s."Id" FROM "BuildingManagementDepositSettlements" s
                WHERE s."SourceContractId"=NEW."ContractId"
                   OR s."TargetContractId"=NEW."ContractId"
                ORDER BY s."Id" FOR SHARE;
              PERFORM p."Id" FROM "BuildingManagementPayments" p
                WHERE p."Id" IN (
                  SELECT s."PaymentId"
                    FROM "BuildingManagementDepositSettlements" s
                   WHERE s."TargetContractId"=NEW."ContractId"
                     AND s."PaymentId" IS NOT NULL)
                   OR (p."ContractId"=NEW."ContractId"
                       AND p."Purpose"=2 AND p."Status"=2)
                ORDER BY p."Id" FOR SHARE;

              IF EXISTS (
                SELECT 1 FROM "BuildingManagementDepositSettlements" s
                 WHERE (s."TargetContractId"=NEW."ContractId"
                   AND (s."Type" IS DISTINCT FROM 3
                     OR s."Status" IS DISTINCT FROM 2
                     OR s."PaymentId" IS NULL))
                    OR (s."SourceContractId"=NEW."ContractId"
                        AND s."Type" IN (1,2,4,5,6))
              ) THEN
                RAISE EXCEPTION 'Prior or pending deposit disposition conflicts with debt review'
                  USING ERRCODE='23514';
              END IF;

              -- Each incoming VND must originate from a completed real
              -- receipt. Reference and disposition checks prevent treating
              -- reservation or another contract's receipt as cash here.
              IF EXISTS (
                SELECT 1
                  FROM "BuildingManagementDepositSettlements" s
                  LEFT JOIN "BuildingManagementPayments" p
                    ON p."Id"=s."PaymentId"
                 WHERE s."TargetContractId"=NEW."ContractId"
                   AND (p."Id" IS NULL OR p."IsDeleted" IS DISTINCT FROM FALSE
                        OR p."Status" IS DISTINCT FROM 2
                        OR p."Amount" <= 0 OR s."Amount" <= 0
                        OR p."PaidAt" IS NULL
                        OR p."PaidAt" > clock_timestamp()
                        OR p."Purpose" NOT IN (1,2)
                        OR p."DepositDisposition" IS NULL
                        OR p."DepositDisposition" NOT IN (1,2)
                        OR (p."Purpose"=2 AND
                            (p."ContractId" IS DISTINCT FROM NEW."ContractId"
                             OR s."SourceRoomReservationId" IS NOT NULL))
                        OR (p."Purpose"=1 AND
                            (p."RoomReservationId" IS NULL
                             OR p."RoomReservationId" IS DISTINCT FROM
                                s."SourceRoomReservationId")))
              ) THEN
                RAISE EXCEPTION 'Deposit allocations require confirmed source receipts'
                  USING ERRCODE='23514';
              END IF;

              -- A completed source receipt must be fully allocated exactly
              -- once to a single contract. A partial/missing allocation,
              -- cross-contract replay, or a refund of the same receipt
              -- cannot manufacture held money.
              IF EXISTS (
                SELECT 1
                  FROM "BuildingManagementDepositSettlements" s
                  JOIN "BuildingManagementPayments" p
                    ON p."Id"=s."PaymentId"
                 WHERE s."TargetContractId"=NEW."ContractId"
                   AND s."Type"=3 AND s."Status"=2
                   AND (
                      (SELECT COALESCE(sum(x."Amount"),0)
                         FROM "BuildingManagementDepositSettlements" x
                        WHERE x."PaymentId"=p."Id"
                          AND x."Type"=3 AND x."Status"=2) <> p."Amount"
                      OR (SELECT count(DISTINCT x."TargetContractId")
                            FROM "BuildingManagementDepositSettlements" x
                           WHERE x."PaymentId"=p."Id"
                             AND x."Type"=3 AND x."Status"=2) <> 1
                      OR EXISTS (
                          SELECT 1 FROM "BuildingManagementDepositSettlements" x
                           WHERE x."PaymentId"=p."Id"
                             AND x."Type" IN (1,2)
                             AND x."Status"=2))
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementPayments" p
                 WHERE p."ContractId"=NEW."ContractId"
                   AND p."Purpose"=2 AND p."Status"=2
                   AND (SELECT COALESCE(sum(s."Amount"),0)
                          FROM "BuildingManagementDepositSettlements" s
                         WHERE s."PaymentId"=p."Id"
                           AND s."TargetContractId"=NEW."ContractId"
                           AND s."Type"=3 AND s."Status"=2) <> p."Amount"
              ) THEN
                RAISE EXCEPTION 'Completed deposit payment is unallocated or double-used'
                  USING ERRCODE='23514';
              END IF;

              SELECT COALESCE(sum(s."Amount"),0) INTO incoming
                FROM "BuildingManagementDepositSettlements" s
               WHERE s."TargetContractId"=NEW."ContractId"
                 AND s."Type"=3 AND s."Status"=2;
              IF incoming <= 0 OR incoming IS DISTINCT FROM NEW."VerifiedHeldAmount" THEN
                RAISE EXCEPTION 'Proposed held cash does not match actual completed deposit allocation'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $cash$;

            CREATE TRIGGER "TR_BM_01_CashBackedDebtReview"
              BEFORE INSERT
              ON "BuildingManagementContractDepositDebtReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOI04CashBackedApproval"();
            CREATE TRIGGER "TR_BM_01_CashBackedOwnerOffset"
              BEFORE INSERT
              ON "BuildingManagementContractDepositDebtOffsetApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOI04CashBackedApproval"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtReviews",
                       "BuildingManagementContractDepositDebtOffsetApprovals"
              IN ACCESS EXCLUSIVE MODE;
            DO $history$
            BEGIN
              IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtReviews" LIMIT 1)
                OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetApprovals" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot remove cash proof from existing signed debt approvals'
                  USING ERRCODE='23514';
              END IF;
            END;
            $history$;
            DROP TRIGGER "TR_BM_01_CashBackedDebtReview"
              ON "BuildingManagementContractDepositDebtReviews";
            DROP TRIGGER "TR_BM_01_CashBackedOwnerOffset"
              ON "BuildingManagementContractDepositDebtOffsetApprovals";
            DROP FUNCTION "BM_GuardOI04CashBackedApproval"();
            """);
    }
}
