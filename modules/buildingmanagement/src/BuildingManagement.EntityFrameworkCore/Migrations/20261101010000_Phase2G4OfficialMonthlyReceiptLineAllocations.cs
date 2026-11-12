using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2G.4: SOURCE-ONLY signed, independently reviewed per-charge
/// allocation of an actual monthly invoice payment. Not an automatic deposit
/// offset. The trigger is deliberately stronger than an EF model validation:
/// every receipt split must be complete at commit, with immutable evidence.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101010000_Phase2G4OfficialMonthlyReceiptLineAllocations")]
public sealed class Phase2G4OfficialMonthlyReceiptLineAllocations : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementOfficialMonthlyReceiptLineAllocations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ReceivablePostingId" uuid NOT NULL
                    REFERENCES "BuildingManagementOfficialInvoiceReceivables"("Id") ON DELETE RESTRICT,
                "ReceiptReviewId" uuid NOT NULL
                    REFERENCES "BuildingManagementOfficialInvoiceReceiptReviews"("Id") ON DELETE RESTRICT,
                "ReceiptAllocationId" uuid NOT NULL,
                "PaymentId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "Category" integer NOT NULL,
                "Amount" numeric(18,0) NOT NULL,
                "AllocatedByUserId" uuid NOT NULL,
                "AllocatedAtUtc" timestamptz NOT NULL,
                "SignedAllocationReference" varchar(500) NOT NULL,
                "InvoiceEvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_OfficialMonthlyLine_Bounds" CHECK (
                    "Category" IN (1,2,3,4)
                    AND "Amount" > 0
                    AND "AllocatedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND length(btrim("SignedAllocationReference")) BETWEEN 12 AND 500
                    AND "InvoiceEvidenceSha256" ~ '^[0-9a-f]{64}$'
                )
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyReceiptLine_ReviewCategory"
                ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                ("ReceiptReviewId","Category");
            CREATE INDEX "IX_BM_MonthlyReceiptLine_InvoiceCategory"
                ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                ("ReceivablePostingId","Category");

            CREATE FUNCTION "BM_GuardOfficialMonthlyReceiptLine"()
            RETURNS trigger LANGUAGE plpgsql AS $line$
            DECLARE p record;
            DECLARE r record;
            DECLARE m record;
            DECLARE c record;
            DECLARE j record;
            DECLARE total_from_review numeric(18,0);
            DECLARE total_in_category numeric(18,0);
            DECLARE line_cap numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'A signed monthly receipt allocation is immutable'
                        USING ERRCODE='23514';
                END IF;

                -- Identical lock ordering for every writer: official invoice
                -- first, then original bank review. Competing requests cannot
                -- both spend one receipt or over-allocate one charge category.
                SELECT * INTO p FROM "BuildingManagementOfficialInvoiceReceivables"
                 WHERE "Id"=NEW."ReceivablePostingId" FOR UPDATE;
                SELECT * INTO r FROM "BuildingManagementOfficialInvoiceReceiptReviews"
                 WHERE "Id"=NEW."ReceiptReviewId" FOR UPDATE;
                SELECT * INTO m FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                 WHERE "Id"=p."IssuedInvoiceId" FOR SHARE;
                SELECT * INTO c FROM "BuildingManagementMonthlyInvoiceCandidates"
                 WHERE "Id"=m."CandidateId" FOR SHARE;
                SELECT * INTO j FROM "BuildingManagementMonthlyInvoicePaymentAllocation"
                 WHERE "Id"=NEW."ReceiptAllocationId" FOR SHARE;

                IF p."Id" IS NULL OR p."Kind" <> 1 OR
                   r."Id" IS NULL OR r."Accepted" IS DISTINCT FROM TRUE OR
                   m."Id" IS NULL OR c."Id" IS NULL OR j."Id" IS NULL OR
                   r."ReceivablePostingId" IS DISTINCT FROM p."Id" OR
                   r."ContractId" IS DISTINCT FROM p."ContractId" OR
                   r."ReceiptAllocationId" IS DISTINCT FROM NEW."ReceiptAllocationId" OR
                   r."PaymentId" IS DISTINCT FROM NEW."PaymentId" OR
                   j."PaymentId" IS DISTINCT FROM NEW."PaymentId" OR
                   j."IssuedInvoiceId" IS DISTINCT FROM p."IssuedInvoiceId" OR
                   j."ContractId" IS DISTINCT FROM p."ContractId" OR
                   j."Amount" IS DISTINCT FROM r."Amount" OR
                   p."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                   m."ContractId" IS DISTINCT FROM p."ContractId" OR
                   c."ContractId" IS DISTINCT FROM p."ContractId" OR
                   p."SourceEvidenceSha256" IS DISTINCT FROM NEW."InvoiceEvidenceSha256" OR
                   c."FrozenEvidenceSha256" IS DISTINCT FROM p."SourceEvidenceSha256" OR
                   NEW."AllocatedByUserId" = r."ReviewedByUserId" OR
                   NEW."AllocatedAtUtc" < r."ReviewedAtUtc" OR
                   NEW."AllocatedAtUtc" > clock_timestamp()
                THEN
                    RAISE EXCEPTION 'Monthly line is not backed by reviewed original invoice payment'
                        USING ERRCODE='23514';
                END IF;

                line_cap := CASE NEW."Category"
                    WHEN 1 THEN c."RentAmount"
                    WHEN 2 THEN c."ElectricityAmount"
                    WHEN 3 THEN c."WaterAmount"
                    WHEN 4 THEN c."AuxiliaryAmount"
                    ELSE NULL END;
                SELECT COALESCE(sum("Amount"),0) INTO total_from_review
                  FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                 WHERE "ReceiptReviewId"=r."Id";
                SELECT COALESCE(sum("Amount"),0) INTO total_in_category
                  FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                 WHERE "ReceivablePostingId"=p."Id"
                   AND "Category"=NEW."Category";
                IF line_cap IS NULL OR NEW."Amount" > line_cap OR
                   total_from_review + NEW."Amount" > r."Amount" OR
                   total_in_category + NEW."Amount" > line_cap
                THEN
                    RAISE EXCEPTION 'Monthly receipt exceeds bank review or archived charge'
                        USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END;
            $line$;

            CREATE TRIGGER "TR_BM_GuardOfficialMonthlyReceiptLine"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOfficialMonthlyReceiptLine"();

            CREATE FUNCTION "BM_VerifyOfficialMonthlyReceiptLineCommit"()
            RETURNS trigger LANGUAGE plpgsql AS $complete$
            DECLARE actual numeric(18,0);
            DECLARE expected numeric(18,0);
            BEGIN
                SELECT "Amount" INTO expected
                  FROM "BuildingManagementOfficialInvoiceReceiptReviews"
                 WHERE "Id"=NEW."ReceiptReviewId";
                SELECT COALESCE(sum("Amount"),0) INTO actual
                  FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                 WHERE "ReceiptReviewId"=NEW."ReceiptReviewId";
                IF actual IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Signed monthly receipt split must equal full reviewed payment at commit'
                        USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END;
            $complete$;
            CREATE CONSTRAINT TRIGGER "TR_BM_VerifyOfficialMonthlyReceiptLineCommit"
              AFTER INSERT ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION "BM_VerifyOfficialMonthlyReceiptLineCommit"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              IN ACCESS EXCLUSIVE MODE;
            DO $historical$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations"
                    LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Cannot downgrade signed monthly receipt line history'
                        USING ERRCODE='23514';
                END IF;
            END;
            $historical$;
            DROP TRIGGER "TR_BM_VerifyOfficialMonthlyReceiptLineCommit"
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            DROP TRIGGER "TR_BM_GuardOfficialMonthlyReceiptLine"
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            DROP FUNCTION "BM_VerifyOfficialMonthlyReceiptLineCommit"();
            DROP FUNCTION "BM_GuardOfficialMonthlyReceiptLine"();
            DROP TABLE "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            """);
    }
}
