using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2G.3 durable debt recognition for both monthly and final issued
/// invoices. Separate append-only posting and manual bank-review events.
/// No production activation, bank API claims or deposit netting.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029226000_Phase2G3OfficialReceivablesAndReceiptReview")]
public sealed class Phase2G3OfficialReceivablesAndReceiptReview : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementOfficialInvoiceReceivables" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Kind" integer NOT NULL,
                "IssuedInvoiceId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "BillingYear" integer NOT NULL,
                "BillingMonth" integer NOT NULL,
                "OriginalAmount" numeric(18,0) NOT NULL,
                "DueDate" date NOT NULL,
                "SourceEvidenceSha256" varchar(64) NOT NULL,
                "PostedByUserId" uuid NOT NULL,
                "PostedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_BM_OfficialReceivable_Valid" CHECK (
                    "Kind" IN (1,2) AND "BillingYear" BETWEEN 2000 AND 2200
                    AND "BillingMonth" BETWEEN 1 AND 12
                    AND "OriginalAmount" > 0
                    AND "OriginalAmount" = trunc("OriginalAmount")
                    AND "SourceEvidenceSha256" ~ '^[0-9a-f]{64}$'
                    AND "PostedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                )
            );
            """);
        builder.Sql("""
            CREATE UNIQUE INDEX "UX_BM_OfficialReceivable_Issued"
              ON "BuildingManagementOfficialInvoiceReceivables"
                 ("Kind","IssuedInvoiceId");
            CREATE UNIQUE INDEX "UX_BM_OfficialReceivable_RoomPeriod"
              ON "BuildingManagementOfficialInvoiceReceivables"
                 ("RoomId","BillingYear","BillingMonth");
            """);
        builder.Sql("""
            CREATE FUNCTION "BM_GuardOfficialReceivablePosting"()
            RETURNS trigger LANGUAGE plpgsql AS $posting$
            DECLARE source record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Official receivable history is immutable'
                      USING ERRCODE = '23514';
                END IF;
                IF NEW."Kind" = 1 THEN
                    SELECT "Id","ContractId","RoomId","BuildingId",
                           "BillingYear","BillingMonth","TotalAmount","DueDate",
                           "CandidateSha256" AS source_hash,
                           "IssuedAtUtc" AS issue_time
                      INTO source
                      FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                     WHERE "Id" = NEW."IssuedInvoiceId" FOR SHARE;
                ELSIF NEW."Kind" = 2 THEN
                    SELECT "Id","ContractId","RoomId","BuildingId",
                           "BillingYear","BillingMonth","TotalAmount","DueDate",
                           "FinancialTermsSha256" AS source_hash,
                           "IssuedAtUtc" AS issue_time
                      INTO source
                      FROM "BuildingManagementIssuedInvoiceLedger"
                     WHERE "Id" = NEW."IssuedInvoiceId" FOR SHARE;
                ELSE
                    RAISE EXCEPTION 'Invalid official receivable kind'
                      USING ERRCODE = '23514';
                END IF;
                IF source."Id" IS NULL
                   OR source."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR source."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR source."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR source."BillingYear" IS DISTINCT FROM NEW."BillingYear"
                   OR source."BillingMonth" IS DISTINCT FROM NEW."BillingMonth"
                   OR source."TotalAmount" IS DISTINCT FROM NEW."OriginalAmount"
                   OR source."DueDate" IS DISTINCT FROM NEW."DueDate"
                   OR source.source_hash IS DISTINCT FROM NEW."SourceEvidenceSha256"
                   OR NEW."PostedAtUtc" < source.issue_time THEN
                    RAISE EXCEPTION 'Official receivable lacks matching issued evidence'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $posting$;
            CREATE TRIGGER "TR_BM_OfficialReceivableImmutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceivables"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOfficialReceivablePosting"();
            """);
        builder.Sql("""
            CREATE TABLE "BuildingManagementOfficialInvoiceReceiptReviews" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ReceivablePostingId" uuid NOT NULL REFERENCES
                   "BuildingManagementOfficialInvoiceReceivables"("Id") ON DELETE RESTRICT,
                "ReceiptAllocationId" uuid NOT NULL,
                "PaymentId" uuid NOT NULL REFERENCES
                   "BuildingManagementPayments"("Id") ON DELETE RESTRICT,
                "ContractId" uuid NOT NULL,
                "Amount" numeric(18,0) NOT NULL,
                "BankTransactionReference" varchar(128) NOT NULL,
                "BankStatementEvidenceReference" varchar(200) NOT NULL,
                "ReviewedByUserId" uuid NOT NULL,
                "ReviewedAtUtc" timestamptz NOT NULL,
                "Accepted" boolean NOT NULL,
                "ReviewReason" varchar(500) NOT NULL,
                CONSTRAINT "CK_BM_OfficialReceiptReview_Valid" CHECK (
                    "Amount" > 0 AND "Amount" = trunc("Amount")
                    AND length(btrim("BankTransactionReference")) BETWEEN 8 AND 128
                    AND length(btrim("BankStatementEvidenceReference")) <= 200
                    AND (NOT "Accepted" OR
                        length(btrim("BankStatementEvidenceReference")) BETWEEN 8 AND 200)
                    AND length(btrim("ReviewReason")) BETWEEN 12 AND 500
                )
            );
            CREATE UNIQUE INDEX "UX_BM_OfficialReceipt_Allocation"
              ON "BuildingManagementOfficialInvoiceReceiptReviews"
                ("ReceivablePostingId","ReceiptAllocationId");
            CREATE UNIQUE INDEX "UX_BM_OfficialReceipt_Payment"
              ON "BuildingManagementOfficialInvoiceReceiptReviews"("PaymentId");
            """);
        builder.Sql("""
            CREATE FUNCTION "BM_GuardOfficialReceiptReview"()
            RETURNS trigger LANGUAGE plpgsql AS $receipt_review$
            DECLARE posting record;
            DECLARE allocation record;
            DECLARE payment record;
            DECLARE approved_delta numeric(18,0);
            DECLARE already_verified numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Official receipt review is immutable'
                      USING ERRCODE = '23514';
                END IF;
                -- SSI detects concurrent inserts into this aggregate even
                -- when both transactions initially read the same sum.
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Official receipt review requires SERIALIZABLE'
                      USING ERRCODE = '23514';
                END IF;
                SELECT * INTO posting
                  FROM "BuildingManagementOfficialInvoiceReceivables"
                 WHERE "Id" = NEW."ReceivablePostingId" FOR UPDATE;
                IF posting."Id" IS NULL THEN
                    RAISE EXCEPTION 'Unknown posted receivable'
                      USING ERRCODE = '23514';
                END IF;
                IF posting."Kind" = 1 THEN
                    SELECT "Id","PaymentId","IssuedInvoiceId","ContractId","Amount",
                           "PaidAtUtc" AS paid_at,
                           "BankTransactionReferenceSnapshot" AS bank_ref,
                           "RecordedByUserId","RecordedAtUtc"
                      INTO allocation
                      FROM "BuildingManagementMonthlyInvoicePaymentAllocation"
                     WHERE "Id" = NEW."ReceiptAllocationId" FOR SHARE;
                ELSE
                    SELECT "Id","PaymentId","IssuedInvoiceId","ContractId","Amount",
                           "PaymentPaidAtUtc" AS paid_at,
                           "PaymentTransactionReferenceSnapshot" AS bank_ref,
                           "RecordedByUserId","RecordedAtUtc"
                      INTO allocation
                      FROM "BuildingManagementInvoicePaymentAllocationJournal"
                     WHERE "Id" = NEW."ReceiptAllocationId" FOR SHARE;
                END IF;
                SELECT * INTO payment FROM "BuildingManagementPayments"
                 WHERE "Id" = NEW."PaymentId" FOR SHARE;
                IF allocation."Id" IS NULL OR payment."Id" IS NULL
                   OR allocation."IssuedInvoiceId" IS DISTINCT FROM posting."IssuedInvoiceId"
                   OR allocation."PaymentId" IS DISTINCT FROM NEW."PaymentId"
                   OR allocation."ContractId" IS DISTINCT FROM posting."ContractId"
                   OR NEW."ContractId" IS DISTINCT FROM posting."ContractId"
                   OR NEW."Amount" IS DISTINCT FROM allocation."Amount"
                   OR NEW."BankTransactionReference" IS DISTINCT FROM allocation.bank_ref
                   OR NEW."ReviewedByUserId" = allocation."RecordedByUserId"
                   OR NEW."ReviewedAtUtc" < allocation."RecordedAtUtc"
                   OR NEW."ReviewedAtUtc" < posting."PostedAtUtc"
                   OR payment."ContractId" IS DISTINCT FROM posting."ContractId"
                   OR payment."InvoiceId" IS DISTINCT FROM posting."IssuedInvoiceId"
                   OR payment."Purpose" <> 3 OR payment."Status" <> 2
                   OR payment."Method" <> 2 OR payment."Channel" <> 1
                   OR payment."IsDeleted" IS DISTINCT FROM FALSE
                   OR payment."Amount" IS DISTINCT FROM allocation."Amount"
                   OR payment."PaidAt" IS DISTINCT FROM allocation.paid_at
                   OR payment."TransactionReference" IS DISTINCT FROM allocation.bank_ref
                   OR payment."Notes" NOT LIKE 'BANK-PROOF: %' THEN
                    RAISE EXCEPTION 'Unanchored or self-reviewed bank receipt'
                      USING ERRCODE = '23514';
                END IF;
                IF NEW."Accepted" THEN
                    SELECT COALESCE(sum(r."Amount"),0) INTO already_verified
                      FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
                     WHERE r."ReceivablePostingId" = posting."Id"
                       AND r."Accepted" = TRUE;
                    approved_delta := 0;
                    IF posting."Kind" = 1 THEN
                        SELECT COALESCE(sum(d."ApprovedSignedDeltaAmount"),0)
                          INTO approved_delta
                          FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
                         WHERE d."IssuedInvoiceId" = posting."IssuedInvoiceId"
                           AND d."Accepted" = TRUE;
                    END IF;
                    IF already_verified + NEW."Amount" >
                       posting."OriginalAmount" + approved_delta THEN
                        RAISE EXCEPTION 'Official receipt would overpay receivable'
                          USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $receipt_review$;
            CREATE TRIGGER "TR_BM_OfficialReceiptReviewImmutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceiptReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOfficialReceiptReview"();
            """);
        builder.Sql("""
            CREATE FUNCTION "BM_RejectOfficialReceivableTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $no_truncate$
            BEGIN
                RAISE EXCEPTION 'Official receivable and receipt review history cannot be truncated'
                  USING ERRCODE = '23514';
            END;
            $no_truncate$;
            CREATE TRIGGER "TR_BM_OfficialReceivableNoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementOfficialInvoiceReceivables"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectOfficialReceivableTruncate"();
            CREATE TRIGGER "TR_BM_OfficialReceiptReviewNoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementOfficialInvoiceReceiptReviews"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectOfficialReceivableTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementOfficialInvoiceReceiptReviews",
                       "BuildingManagementOfficialInvoiceReceivables"
              IN ACCESS EXCLUSIVE MODE;
            DO $official_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementOfficialInvoiceReceivables" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM
                    "BuildingManagementOfficialInvoiceReceiptReviews" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Official receivable audit exists; rollback forbidden'
                      USING ERRCODE = '23514';
                END IF;
            END;
            $official_down$;
            DROP TRIGGER "TR_BM_OfficialReceiptReviewNoTruncate"
              ON "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP TRIGGER "TR_BM_OfficialReceivableNoTruncate"
              ON "BuildingManagementOfficialInvoiceReceivables";
            DROP FUNCTION "BM_RejectOfficialReceivableTruncate"();
            DROP TRIGGER "TR_BM_OfficialReceiptReviewImmutable"
              ON "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP FUNCTION "BM_GuardOfficialReceiptReview"();
            DROP TRIGGER "TR_BM_OfficialReceivableImmutable"
              ON "BuildingManagementOfficialInvoiceReceivables";
            DROP FUNCTION "BM_GuardOfficialReceivablePosting"();
            DROP TABLE "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP TABLE "BuildingManagementOfficialInvoiceReceivables";
            """);
    }
}
