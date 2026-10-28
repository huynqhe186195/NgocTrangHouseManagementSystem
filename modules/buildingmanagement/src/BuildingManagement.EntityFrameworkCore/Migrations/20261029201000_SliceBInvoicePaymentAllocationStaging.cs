using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive, SOURCE-ONLY staging for auditable invoice-payment receipts.
/// This is NOT an issued invoice, recognized allocation, receivable, debt
/// reduction or refund. No runtime writer, posting route or DB rollout.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029201000_SliceBInvoicePaymentAllocationStaging")]
public sealed class SliceBInvoicePaymentAllocationStaging : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementInvoicePaymentAllocationJournal" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PaymentId" uuid NOT NULL,
                "IssuedInvoiceId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "Amount" numeric(18,0) NOT NULL,
                "PaymentNumberSnapshot" varchar(64) NOT NULL,
                "PaymentPaidAtUtc" timestamptz NOT NULL,
                "PaymentTransactionReferenceSnapshot" varchar(128) NULL,
                "RecordedByUserId" uuid NOT NULL,
                "RecordedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "FK_BM_InvoiceAllocation_Payment"
                    FOREIGN KEY ("PaymentId")
                    REFERENCES "BuildingManagementPayments" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_InvoiceAllocation_Positive"
                    CHECK ("Amount" > 0),
                CONSTRAINT "CK_BM_InvoiceAllocation_PaymentNumber"
                    CHECK (length(trim("PaymentNumberSnapshot")) BETWEEN 1 AND 64),
                CONSTRAINT "CK_BM_InvoiceAllocation_Ids"
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "PaymentId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "IssuedInvoiceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ContractId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "RecordedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                CONSTRAINT "CK_BM_InvoiceAllocation_Chronology"
                    CHECK ("RecordedAtUtc" >= "PaymentPaidAtUtc")
            );
            CREATE UNIQUE INDEX "UX_BM_InvoiceAllocation_Payment"
                ON "BuildingManagementInvoicePaymentAllocationJournal" ("PaymentId");
            CREATE UNIQUE INDEX "UX_BM_InvoiceAllocation_Invoice_Payment"
                ON "BuildingManagementInvoicePaymentAllocationJournal"
                ("IssuedInvoiceId", "PaymentId");
            CREATE INDEX "IX_BM_InvoiceAllocation_Contract"
                ON "BuildingManagementInvoicePaymentAllocationJournal" ("ContractId");

            -- Stage only verified receipt metadata; never use a draft
            -- invoice to infer legal issue or post receivable balances.
            -- Lock the source payment until insert transaction commit.
            -- Later source changes are detected by the read-only auditor,
            -- and are NOT automatically treated as recognized money.
            CREATE FUNCTION "BM_GuardStagedInvoiceAllocation"()
            RETURNS trigger LANGUAGE plpgsql AS $allocation_guard$
            DECLARE receipt record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Staged allocation journal is append-only'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO receipt
                  FROM "BuildingManagementPayments"
                 WHERE "Id" = NEW."PaymentId"
                 FOR SHARE;
                IF NOT FOUND
                   OR receipt."Purpose" <> 3
                   OR receipt."Status" <> 2
                   OR receipt."IsDeleted" IS DISTINCT FROM FALSE
                   OR receipt."InvoiceId" IS DISTINCT FROM NEW."IssuedInvoiceId"
                   OR receipt."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR receipt."Amount" IS DISTINCT FROM NEW."Amount"
                   OR receipt."PaymentNumber" IS DISTINCT FROM NEW."PaymentNumberSnapshot"
                   OR receipt."PaidAt" IS NULL
                   OR receipt."PaidAt" IS DISTINCT FROM NEW."PaymentPaidAtUtc"
                   OR receipt."TransactionReference" IS DISTINCT FROM
                      NEW."PaymentTransactionReferenceSnapshot" THEN
                    RAISE EXCEPTION 'Staged allocation requires an exact completed invoice receipt'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $allocation_guard$;
            CREATE TRIGGER "TR_BM_InvoiceAllocation_InsertOrReject"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementInvoicePaymentAllocationJournal"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardStagedInvoiceAllocation"();
            CREATE TRIGGER "TR_BM_InvoiceAllocation_BlockTruncate"
                BEFORE TRUNCATE
                ON "BuildingManagementInvoicePaymentAllocationJournal"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardStagedInvoiceAllocation"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementInvoicePaymentAllocationJournal"
                IN ACCESS EXCLUSIVE MODE;
            DO $allocation_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal" LIMIT 1) THEN
                    RAISE EXCEPTION 'Staged payment allocation history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $allocation_down$;
            DROP TABLE "BuildingManagementInvoicePaymentAllocationJournal";
            DROP FUNCTION "BM_GuardStagedInvoiceAllocation"();
            """);
    }
}
