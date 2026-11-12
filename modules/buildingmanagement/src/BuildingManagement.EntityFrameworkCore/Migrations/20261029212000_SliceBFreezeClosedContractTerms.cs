using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only contract history guard after immutable deposit closeout.
/// Closeout writes its approval before Contract.End within the SAME UoW:
/// permit the single Active-to-Ended transition, not a financial rewrite.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029212000_SliceBFreezeClosedContractTerms")]
public sealed class SliceBFreezeClosedContractTerms : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_GuardClosedContractSource"()
            RETURNS trigger LANGUAGE plpgsql AS $contract_guard$
            DECLARE approved_at timestamptz;
            BEGIN
                -- A row UPDATE/DELETE already holds the Contract row lock.
                -- The closeout approval INSERT locks the SAME contract row;
                -- their operations therefore serialize until commit.
                SELECT "ApprovedAtUtc" INTO approved_at
                  FROM "BuildingManagementContractDepositCloseoutApprovals"
                 WHERE "ContractId" = OLD."Id";
                IF NOT FOUND THEN
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END IF;

                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Signed closeout contract cannot be deleted'
                        USING ERRCODE = '23514';
                END IF;

                -- A signed financial closeout cannot outlive a rewritten
                -- contract source, even through direct SQL/soft deletion.
                IF NEW."Id" IS DISTINCT FROM OLD."Id"
                   OR NEW."ContractNumber" IS DISTINCT FROM OLD."ContractNumber"
                   OR NEW."RoomId" IS DISTINCT FROM OLD."RoomId"
                   OR NEW."RenewedFromContractId" IS DISTINCT FROM OLD."RenewedFromContractId"
                   OR NEW."StartDate" IS DISTINCT FROM OLD."StartDate"
                   OR NEW."EndDate" IS DISTINCT FROM OLD."EndDate"
                   OR NEW."MonthlyRent" IS DISTINCT FROM OLD."MonthlyRent"
                   OR NEW."DepositAmount" IS DISTINCT FROM OLD."DepositAmount"
                   OR NEW."Notes" IS DISTINCT FROM OLD."Notes"
                   OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted"
                THEN
                    RAISE EXCEPTION 'Signed closeout freezes contract financial and scope terms'
                        USING ERRCODE = '23514';
                END IF;

                -- ABP may flush audited metadata on an unchanged Active
                -- contract after approval INSERT and before Contract.End.
                -- The separate deferred commit trigger requires that
                -- Active state cannot survive the enclosing transaction.
                IF OLD."Status" = 2 AND NEW."Status" = 2
                   AND OLD."ActualEndedAt" IS NULL AND NEW."ActualEndedAt" IS NULL
                   AND OLD."EndReason" IS NULL AND NEW."EndReason" IS NULL
                THEN
                    RETURN NEW;
                END IF;

                -- The single expected transition runs AFTER the approval
                -- INSERT and BEFORE the transaction commits. The UoW may
                -- still update audit metadata without changing these facts.
                IF NEW."Status" <> 3
                   OR NEW."ActualEndedAt" IS NULL
                   OR NEW."EndReason" IS NULL
                   OR NEW."ActualEndedAt" < approved_at
                   OR (OLD."Status" = 3 AND
                       (NEW."ActualEndedAt" IS DISTINCT FROM OLD."ActualEndedAt"
                        OR NEW."EndReason" IS DISTINCT FROM OLD."EndReason"))
                   OR OLD."Status" NOT IN (2, 3)
                THEN
                    RAISE EXCEPTION 'Signed closeout permits only final contract end and audit-only updates'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $contract_guard$;

            CREATE TRIGGER "TR_BM_ClosedContract_FreezeSource"
                BEFORE UPDATE OR DELETE ON "BuildingManagementContracts"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardClosedContractSource"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContracts",
                       "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $contract_guard_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed contract closeout exists; refuse contract freeze downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $contract_guard_down$;
            DROP TRIGGER "TR_BM_ClosedContract_FreezeSource"
                ON "BuildingManagementContracts";
            DROP FUNCTION "BM_GuardClosedContractSource"();
            """);
    }
}
