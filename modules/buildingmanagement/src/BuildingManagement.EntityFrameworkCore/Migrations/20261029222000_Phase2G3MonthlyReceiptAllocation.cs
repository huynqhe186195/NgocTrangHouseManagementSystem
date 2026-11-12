using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Immutable monthly receipt-to-invoice allocation. This remains an
/// explicitly attested MANUAL bank payment in ISOLATED STAGING; neither a
/// bank callback nor a posted official receivable is enabled here.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029222000_Phase2G3MonthlyReceiptAllocation")]
public sealed class Phase2G3MonthlyReceiptAllocation : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementMonthlyInvoicePaymentAllocation" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PaymentId" uuid NOT NULL REFERENCES
                    "BuildingManagementPayments" ("Id") ON DELETE RESTRICT,
                "IssuedInvoiceId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyIssuedInvoiceLedger" ("Id") ON DELETE RESTRICT,
                "ContractId" uuid NOT NULL,
                "Amount" numeric(18,0) NOT NULL,
                "PaymentNumberSnapshot" varchar(64) NOT NULL,
                "PaidAtUtc" timestamptz NOT NULL,
                "BankTransactionReferenceSnapshot" varchar(128) NOT NULL,
                "RecordedByUserId" uuid NOT NULL,
                "RecordedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_BM_MonthlyPayment_Valid"
                    CHECK ("Amount" > 0
                       AND "Amount" = trunc("Amount")
                       AND length(btrim("PaymentNumberSnapshot")) > 0
                       AND length(btrim("BankTransactionReferenceSnapshot")) >= 8
                       AND "RecordedAtUtc" >= "PaidAtUtc")
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyAllocation_Payment"
                ON "BuildingManagementMonthlyInvoicePaymentAllocation" ("PaymentId");
            CREATE INDEX "IX_BM_MonthlyAllocation_InvoiceDate"
                ON "BuildingManagementMonthlyInvoicePaymentAllocation"
                   ("IssuedInvoiceId","RecordedAtUtc");

            CREATE FUNCTION "BM_GuardMonthlyPaymentAllocation"()
            RETURNS trigger LANGUAGE plpgsql AS $allocation_guard$
            DECLARE invoice record;
            DECLARE payment record;
            DECLARE already_allocated numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly payment allocation history is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO invoice
                  FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                 WHERE "Id" = NEW."IssuedInvoiceId" FOR UPDATE;
                SELECT * INTO payment FROM "BuildingManagementPayments"
                 WHERE "Id" = NEW."PaymentId" FOR SHARE;
                IF invoice."Id" IS NULL OR payment."Id" IS NULL
                   OR NEW."ContractId" IS DISTINCT FROM invoice."ContractId"
                   OR payment."ContractId" IS DISTINCT FROM invoice."ContractId"
                   OR payment."InvoiceId" IS DISTINCT FROM invoice."Id"
                   OR payment."Purpose" <> 3 OR payment."Status" <> 2
                   OR payment."IsDeleted" IS DISTINCT FROM FALSE
                   OR payment."Amount" IS DISTINCT FROM NEW."Amount"
                   OR payment."PaidAt" IS DISTINCT FROM NEW."PaidAtUtc"
                   OR payment."PaymentNumber" IS DISTINCT FROM NEW."PaymentNumberSnapshot"
                   OR payment."TransactionReference" IS DISTINCT FROM
                       NEW."BankTransactionReferenceSnapshot"
                   OR NEW."PaidAtUtc" < invoice."IssuedAtUtc"
                   OR NEW."RecordedAtUtc" < NEW."PaidAtUtc" THEN
                    RAISE EXCEPTION 'Monthly allocation lacks a matching completed bank receipt'
                        USING ERRCODE = '23514';
                END IF;

                SELECT COALESCE(sum(a."Amount"),0)
                  INTO already_allocated
                  FROM "BuildingManagementMonthlyInvoicePaymentAllocation" a
                 WHERE a."IssuedInvoiceId" = invoice."Id";
                IF already_allocated + NEW."Amount" > invoice."TotalAmount" THEN
                    RAISE EXCEPTION 'Monthly allocation would exceed immutable invoice total'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $allocation_guard$;
            CREATE TRIGGER "TR_BM_MonthlyAllocationImmutable"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementMonthlyInvoicePaymentAllocation"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyPaymentAllocation"();

            -- Original receipt cannot be edited, soft-deleted or removed
            -- after an immutable monthly journal entry refers to it.
            CREATE FUNCTION "BM_GuardMonthlyReceiptSource"()
            RETURNS trigger LANGUAGE plpgsql AS $receipt_guard$
            BEGIN
                IF TG_OP IN ('UPDATE','DELETE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyInvoicePaymentAllocation"
                     WHERE "PaymentId" = OLD."Id"
                ) THEN
                    RAISE EXCEPTION 'Allocated monthly bank receipt is immutable'
                        USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'TRUNCATE' THEN
                    IF EXISTS (
                        SELECT 1 FROM "BuildingManagementMonthlyInvoicePaymentAllocation" LIMIT 1
                    ) THEN
                        RAISE EXCEPTION 'Allocated monthly receipt history cannot be truncated'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $receipt_guard$;
            CREATE TRIGGER "TR_BM_MonthlyReceiptFreeze"
                BEFORE UPDATE OR DELETE ON "BuildingManagementPayments"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyReceiptSource"();
            CREATE TRIGGER "TR_BM_MonthlyReceiptFreezeTruncate"
                BEFORE TRUNCATE ON "BuildingManagementPayments"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardMonthlyReceiptSource"();

            CREATE FUNCTION "BM_RejectMonthlyAllocationTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $truncate$
            BEGIN
                RAISE EXCEPTION 'Monthly allocation history cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $truncate$;
            CREATE TRIGGER "TR_BM_MonthlyAllocationNoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementMonthlyInvoicePaymentAllocation"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectMonthlyAllocationTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMonthlyInvoicePaymentAllocation",
                       "BuildingManagementPayments"
                IN ACCESS EXCLUSIVE MODE;
            DO $monthly_allocation_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyInvoicePaymentAllocation" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Monthly receipt journal exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $monthly_allocation_down$;
            DROP TRIGGER "TR_BM_MonthlyReceiptFreezeTruncate"
                ON "BuildingManagementPayments";
            DROP TRIGGER "TR_BM_MonthlyReceiptFreeze"
                ON "BuildingManagementPayments";
            DROP FUNCTION "BM_GuardMonthlyReceiptSource"();
            DROP TRIGGER "TR_BM_MonthlyAllocationNoTruncate"
                ON "BuildingManagementMonthlyInvoicePaymentAllocation";
            DROP FUNCTION "BM_RejectMonthlyAllocationTruncate"();
            DROP TRIGGER "TR_BM_MonthlyAllocationImmutable"
                ON "BuildingManagementMonthlyInvoicePaymentAllocation";
            DROP FUNCTION "BM_GuardMonthlyPaymentAllocation"();
            DROP TABLE "BuildingManagementMonthlyInvoicePaymentAllocation";
            """);
    }
}
