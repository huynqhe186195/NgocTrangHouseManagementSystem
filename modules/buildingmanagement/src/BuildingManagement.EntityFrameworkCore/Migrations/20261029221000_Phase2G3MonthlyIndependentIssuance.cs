using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Two-person reviewed and immutable MONTHLY staging issuance. Never
/// migrates production; bank verification, recognized receivable and
/// adjustments require separate authorized posting/audit workflows.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029221000_Phase2G3MonthlyIndependentIssuance")]
public sealed class Phase2G3MonthlyIndependentIssuance : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementMonthlyInvoiceReviews" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CandidateId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyInvoiceCandidates"("Id") ON DELETE RESTRICT,
                "ContractId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "CandidateSha256" varchar(64) NOT NULL,
                "PreparedByUserId" uuid NOT NULL,
                "ReviewedByUserId" uuid NOT NULL,
                "ReviewedAtUtc" timestamptz NOT NULL,
                "Accepted" boolean NOT NULL,
                "Reason" varchar(500) NOT NULL,
                CONSTRAINT "CK_BM_MonthlyReview_DistinctActors"
                    CHECK ("PreparedByUserId" <> "ReviewedByUserId"
                           AND length(btrim("Reason")) BETWEEN 8 AND 500)
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyReview_Candidate"
                ON "BuildingManagementMonthlyInvoiceReviews" ("CandidateId");

            CREATE TABLE "BuildingManagementMonthlyIssuedInvoiceLedger" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CandidateId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyInvoiceCandidates"("Id") ON DELETE RESTRICT,
                "ReviewId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyInvoiceReviews"("Id") ON DELETE RESTRICT,
                "DraftInvoiceId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "BillingYear" integer NOT NULL,
                "BillingMonth" integer NOT NULL,
                "InvoiceNumber" varchar(64) NOT NULL,
                "IssuedByUserId" uuid NOT NULL,
                "IssuedAtUtc" timestamptz NOT NULL,
                "DueDate" date NOT NULL,
                "TotalAmount" numeric(18,0) NOT NULL,
                "CandidateSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_MonthlyIssued_MoneyAndDates"
                    CHECK ("TotalAmount" > 0 AND "TotalAmount" = trunc("TotalAmount")
                           AND length(trim("InvoiceNumber")) BETWEEN 5 AND 64
                           AND "DueDate" >=
                             ("IssuedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyIssued_Candidate"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"("CandidateId");
            CREATE UNIQUE INDEX "UX_BM_MonthlyIssued_Review"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"("ReviewId");
            CREATE UNIQUE INDEX "UX_BM_MonthlyIssued_Number"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"("InvoiceNumber");
            CREATE UNIQUE INDEX "UX_BM_MonthlyIssued_ContractPeriod"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"
                    ("ContractId","BillingYear","BillingMonth");
            CREATE UNIQUE INDEX "UX_BM_MonthlyIssued_RoomPeriod"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"
                    ("RoomId","BillingYear","BillingMonth");

            -- The review and issuer must match the ORIGINAL archive.
            -- Row locks persist through commit; no races with changes to
            -- the candidate or to a same-period final invoice.
            CREATE FUNCTION "BM_GuardMonthlyInvoiceReview"()
            RETURNS trigger LANGUAGE plpgsql AS $monthly_review$
            DECLARE source record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly independent review is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO source FROM "BuildingManagementMonthlyInvoiceCandidates"
                 WHERE "Id" = NEW."CandidateId" FOR SHARE;
                IF NOT FOUND OR NEW."ContractId" IS DISTINCT FROM source."ContractId"
                   OR NEW."BuildingId" IS DISTINCT FROM source."BuildingId"
                   OR NEW."CandidateSha256" IS DISTINCT FROM source."FrozenEvidenceSha256"
                   OR NEW."PreparedByUserId" IS DISTINCT FROM source."ReviewedByUserId"
                   OR NEW."ReviewedAtUtc" < source."ReviewedAtUtc" THEN
                    RAISE EXCEPTION 'Monthly review source or original preparer does not match'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $monthly_review$;
            CREATE TRIGGER "TR_BM_MonthlyReviewImmutable"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementMonthlyInvoiceReviews"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyInvoiceReview"();

            CREATE FUNCTION "BM_GuardMonthlyIssuedInvoice"()
            RETURNS trigger LANGUAGE plpgsql AS $monthly_issued$
            DECLARE source record;
            DECLARE review record;
            DECLARE draft record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly issued invoice is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO draft FROM "BuildingManagementInvoices"
                 WHERE "Id" = NEW."DraftInvoiceId" FOR UPDATE;
                SELECT * INTO source FROM "BuildingManagementMonthlyInvoiceCandidates"
                 WHERE "Id" = NEW."CandidateId" FOR SHARE;
                SELECT * INTO review FROM "BuildingManagementMonthlyInvoiceReviews"
                 WHERE "Id" = NEW."ReviewId" FOR SHARE;
                IF draft."Id" IS NULL OR source."Id" IS NULL OR review."Id" IS NULL
                   OR draft."Status" <> 0
                   OR source."DraftInvoiceId" IS DISTINCT FROM draft."Id"
                   OR source."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR source."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR source."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR source."BillingYear" IS DISTINCT FROM NEW."BillingYear"
                   OR source."BillingMonth" IS DISTINCT FROM NEW."BillingMonth"
                   OR source."DraftInvoiceId" IS DISTINCT FROM NEW."DraftInvoiceId"
                   OR source."TotalAmount" IS DISTINCT FROM NEW."TotalAmount"
                   OR source."DueDate" IS DISTINCT FROM NEW."DueDate"
                   OR source."FrozenEvidenceSha256" IS DISTINCT FROM NEW."CandidateSha256"
                   OR review."CandidateId" IS DISTINCT FROM source."Id"
                   OR review."CandidateSha256" IS DISTINCT FROM source."FrozenEvidenceSha256"
                   OR review."ContractId" IS DISTINCT FROM source."ContractId"
                   OR review."BuildingId" IS DISTINCT FROM source."BuildingId"
                   OR review."Accepted" IS DISTINCT FROM TRUE
                   OR NEW."IssuedByUserId" = review."ReviewedByUserId"
                   OR NEW."IssuedAtUtc" < review."ReviewedAtUtc"
                   OR EXISTS (
                       SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                        WHERE (i."RoomId" = NEW."RoomId" OR
                               i."ContractId" = NEW."ContractId")
                          AND i."BillingYear" = NEW."BillingYear"
                          AND i."BillingMonth" = NEW."BillingMonth"
                   ) THEN
                    RAISE EXCEPTION 'Monthly issuance lacks independent approval or conflicts with final invoice'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $monthly_issued$;
            CREATE TRIGGER "TR_BM_MonthlyIssuedImmutable"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementMonthlyIssuedInvoiceLedger"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyIssuedInvoice"();

            -- Existing final-invoice trigger already locks the SAME draft.
            -- This second trigger prevents final issuance after monthly
            -- issuance from bypassing the cross-ledger period exclusivity.
            CREATE FUNCTION "BM_GuardFinalInvoiceAgainstMonthly"()
            RETURNS trigger LANGUAGE plpgsql AS $final_month_collision$
            BEGIN
                IF TG_OP = 'INSERT' AND EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" m
                     WHERE (m."ContractId" = NEW."ContractId"
                           OR m."RoomId" = NEW."RoomId")
                       AND m."BillingYear" = NEW."BillingYear"
                       AND m."BillingMonth" = NEW."BillingMonth"
                ) THEN
                    RAISE EXCEPTION 'Final invoice conflicts with issued monthly invoice'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $final_month_collision$;
            CREATE TRIGGER "TR_BM_FinalInvoiceNoMonthlyCollision"
                BEFORE INSERT ON "BuildingManagementIssuedInvoiceLedger"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardFinalInvoiceAgainstMonthly"();

            -- Do not permit bypass of immutable history through TRUNCATE.
            CREATE FUNCTION "BM_RejectMonthlyFinanceTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $monthly_truncate$
            BEGIN
                RAISE EXCEPTION 'Monthly financial history cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $monthly_truncate$;
            CREATE TRIGGER "TR_BM_MonthlyReviewNoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementMonthlyInvoiceReviews"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectMonthlyFinanceTruncate"();
            CREATE TRIGGER "TR_BM_MonthlyIssuedNoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementMonthlyIssuedInvoiceLedger"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectMonthlyFinanceTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMonthlyIssuedInvoiceLedger",
                       "BuildingManagementMonthlyInvoiceReviews"
                IN ACCESS EXCLUSIVE MODE;
            DO $monthly_issuance_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementMonthlyInvoiceReviews" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Reviewed or issued monthly history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $monthly_issuance_down$;

            DROP TRIGGER "TR_BM_FinalInvoiceNoMonthlyCollision"
                ON "BuildingManagementIssuedInvoiceLedger";
            DROP FUNCTION "BM_GuardFinalInvoiceAgainstMonthly"();
            DROP TRIGGER "TR_BM_MonthlyIssuedNoTruncate"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_MonthlyReviewNoTruncate"
                ON "BuildingManagementMonthlyInvoiceReviews";
            DROP FUNCTION "BM_RejectMonthlyFinanceTruncate"();
            DROP TRIGGER "TR_BM_MonthlyIssuedImmutable"
                ON "BuildingManagementMonthlyIssuedInvoiceLedger";
            DROP FUNCTION "BM_GuardMonthlyIssuedInvoice"();
            DROP TRIGGER "TR_BM_MonthlyReviewImmutable"
                ON "BuildingManagementMonthlyInvoiceReviews";
            DROP FUNCTION "BM_GuardMonthlyInvoiceReview"();
            DROP TABLE "BuildingManagementMonthlyIssuedInvoiceLedger";
            DROP TABLE "BuildingManagementMonthlyInvoiceReviews";
            """);
    }
}
