using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// SOURCE-ONLY final invoice to completed receipt ledger link.
/// No financial posting/writer is activated by this schema migration.
/// The view is a candidate reconciliation, NOT a recognized receivable.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029203000_SliceBInvoiceReconciliationBoundary")]
public sealed class SliceBInvoiceReconciliationBoundary : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Previously staged receipt journal entries were deliberately
            -- not FK-linked to a DRAFT. Now require a real issued-ledger ID.
            -- Existing invalid staging references cause an atomic migration
            -- failure rather than silently being treated as customer funds.
            ALTER TABLE "BuildingManagementInvoicePaymentAllocationJournal"
                ADD CONSTRAINT "FK_BM_Allocation_ActualIssuedInvoice"
                FOREIGN KEY ("IssuedInvoiceId")
                REFERENCES "BuildingManagementIssuedInvoiceLedger" ("Id")
                ON DELETE RESTRICT;

            -- Invoice amounts must be representable as full VND, because
            -- Payment.Amount/receipt allocation is numeric(18,0). Neither
            -- rounding nor fractional debt is implicitly authorized.
            ALTER TABLE "BuildingManagementIssuedInvoiceLedger"
                ADD CONSTRAINT "CK_BM_IssuedLedger_WholeVnd"
                CHECK ("BaseAmount" = trunc("BaseAmount")
                   AND "AuxiliaryAmount" = trunc("AuxiliaryAmount")
                   AND "TotalAmount" = trunc("TotalAmount"));

            -- Snapshot metadata must match posted columns exactly. The
            -- SHA digest must additionally be checked by the application
            -- before ANY future issuer transaction may run.
            ALTER TABLE "BuildingManagementIssuedInvoiceLedger"
                ADD CONSTRAINT "CK_BM_IssuedLedger_JsonMatchesColumns"
                CHECK (
                   "FinancialTermsJson"::jsonb->>'DraftInvoiceId'
                       = "DraftInvoiceId"::text
                   AND "FinancialTermsJson"::jsonb->>'EvidenceSha256'
                       = "EvidenceSha256"
                   AND ("FinancialTermsJson"::jsonb->>'BaseAmount')::numeric
                       = "BaseAmount"
                   AND ("FinancialTermsJson"::jsonb->>'AuxiliaryAmount')::numeric
                       = "AuxiliaryAmount"
                   AND ("FinancialTermsJson"::jsonb->>'TotalAmount')::numeric
                       = "TotalAmount"
                   AND ("FinancialTermsJson"::jsonb->>'DueDate')::date
                       = "DueDate"
                   AND "FinancialTermsJson"::jsonb ? 'DraftInvoiceId'
                   AND "FinancialTermsJson"::jsonb ? 'EvidenceSha256'
                   AND "FinancialTermsJson"::jsonb ? 'BaseAmount'
                   AND "FinancialTermsJson"::jsonb ? 'AuxiliaryAmount'
                   AND "FinancialTermsJson"::jsonb ? 'TotalAmount'
                   AND "FinancialTermsJson"::jsonb ? 'DueDate'
                );

            CREATE FUNCTION "BM_GuardAllocationIssuedSourceAndCeiling"()
            RETURNS trigger LANGUAGE plpgsql AS $invoice_ceiling$
            DECLARE source_issued record;
            DECLARE already_allocated numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Recognized allocation source is immutable'
                        USING ERRCODE = '23514';
                END IF;
                -- Lock issued invoice first, and keep lock until COMMIT.
                -- Same-invoice concurrent allocations cannot both pass the
                -- amount ceiling after the first allocates its payment.
                SELECT * INTO source_issued
                  FROM "BuildingManagementIssuedInvoiceLedger"
                 WHERE "Id" = NEW."IssuedInvoiceId" FOR UPDATE;
                IF NOT FOUND
                   OR source_issued."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR NEW."Amount" <= 0
                   OR NEW."RecordedAtUtc" < source_issued."IssuedAtUtc" THEN
                    RAISE EXCEPTION 'Allocation must reference an issued invoice in the exact contract'
                        USING ERRCODE = '23514';
                END IF;

                SELECT COALESCE(sum(a."Amount"),0)
                  INTO already_allocated
                  FROM "BuildingManagementInvoicePaymentAllocationJournal" a
                 WHERE a."IssuedInvoiceId" = NEW."IssuedInvoiceId";
                IF already_allocated + NEW."Amount" > source_issued."TotalAmount" THEN
                    RAISE EXCEPTION 'Allocation would exceed immutable invoice total'
                        USING ERRCODE = '23514';
                END IF;
                -- The previous staging trigger independently verifies the
                -- Payment receipt is Completed and attributed to this
                -- SAME issued invoice, not to the old Draft invoice ID.
                RETURN NEW;
            END;
            $invoice_ceiling$;
            CREATE TRIGGER "TR_BM_InvoiceAllocation_IssuedSourceAndCeiling"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementInvoicePaymentAllocationJournal"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardAllocationIssuedSourceAndCeiling"();

            -- Read-only candidate figures. This is NOT a posted receivable
            -- or proof of deposit-settlement authorization.
            CREATE VIEW "BuildingManagementInvoiceAllocationCandidateBalances" AS
            SELECT i."Id" AS "IssuedInvoiceId",
                   i."ContractId" AS "ContractId",
                   i."RoomId" AS "RoomId",
                   i."MoveOutRequestId" AS "MoveOutRequestId",
                   i."TotalAmount" AS "InvoiceTotal",
                   COALESCE(sum(a."Amount"),0)::numeric(18,0) AS "StagedAllocated",
                   greatest(i."TotalAmount" - COALESCE(sum(a."Amount"),0),0)
                       AS "CandidateUnallocated",
                   count(a."Id") AS "StagedReceiptCount",
                   (i."TotalAmount" = COALESCE(sum(a."Amount"),0))
                       AS "AllReceiptsAccountedInStaging",
                   FALSE AS "IsDebtLegallyCleared",
                   FALSE AS "MaySettleDeposit",
                   FALSE AS "MayCompleteMoveOut"
            FROM "BuildingManagementIssuedInvoiceLedger" i
            LEFT JOIN "BuildingManagementInvoicePaymentAllocationJournal" a
                   ON a."IssuedInvoiceId" = i."Id"
            GROUP BY i."Id", i."ContractId", i."RoomId",
                     i."MoveOutRequestId", i."TotalAmount";
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementInvoicePaymentAllocationJournal",
                       "BuildingManagementIssuedInvoiceLedger"
                IN ACCESS EXCLUSIVE MODE;
            DO $allocation_link_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Issued invoice or allocated receipt history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $allocation_link_down$;
            DROP VIEW "BuildingManagementInvoiceAllocationCandidateBalances";
            DROP TRIGGER "TR_BM_InvoiceAllocation_IssuedSourceAndCeiling"
                ON "BuildingManagementInvoicePaymentAllocationJournal";
            DROP FUNCTION "BM_GuardAllocationIssuedSourceAndCeiling"();
            ALTER TABLE "BuildingManagementInvoicePaymentAllocationJournal"
                DROP CONSTRAINT "FK_BM_Allocation_ActualIssuedInvoice";
            ALTER TABLE "BuildingManagementIssuedInvoiceLedger"
                DROP CONSTRAINT "CK_BM_IssuedLedger_JsonMatchesColumns",
                DROP CONSTRAINT "CK_BM_IssuedLedger_WholeVnd";
            """);
    }
}
