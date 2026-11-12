using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// ADDITIVE SOURCE-ONLY Slice B final-invoice EVIDENCE persistence.
/// This is NOT invoice issuance, debt recognition, refund authorization or
/// permission to enable HTTP writers. Do not deploy to an application DB.
/// PostgreSQL acceptance runs only in a rolled-back random scratch schema.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029200000_SliceBFinalInvoiceEvidencePersistence")]
public sealed class SliceBFinalInvoiceEvidencePersistence : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementFinalInvoiceIssueEvidence" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DraftInvoiceId" uuid NOT NULL,
                "MoveOutRequestId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BillingYear" integer NOT NULL,
                "BillingMonth" integer NOT NULL,
                "PreparedByUserId" uuid NOT NULL,
                "PreparedAtUtc" timestamptz NOT NULL,
                "KnownBaseChargeTotal" numeric(18,3) NOT NULL,
                "EvidenceJson" text NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "FK_BM_FinalIssueEvidence_Draft"
                    FOREIGN KEY ("DraftInvoiceId")
                    REFERENCES "BuildingManagementInvoices" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_FinalIssueEvidence_Request"
                    FOREIGN KEY ("MoveOutRequestId")
                    REFERENCES "BuildingManagementMoveOutRequests" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_FinalIssueEvidence_Positive"
                    CHECK ("KnownBaseChargeTotal" > 0),
                CONSTRAINT "CK_BM_FinalIssueEvidence_Period"
                    CHECK ("BillingYear" BETWEEN 2000 AND 2200
                           AND "BillingMonth" BETWEEN 1 AND 12),
                CONSTRAINT "CK_BM_FinalIssueEvidence_Json"
                    CHECK (length("EvidenceJson") BETWEEN 1 AND 1000000),
                CONSTRAINT "CK_BM_FinalIssueEvidence_Hash"
                    CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                CONSTRAINT "CK_BM_FinalIssueEvidence_Ids"
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "DraftInvoiceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "MoveOutRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ContractId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "RoomId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "PreparedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid)
            );
            CREATE UNIQUE INDEX "UX_BM_FinalIssueEvidence_Draft"
                ON "BuildingManagementFinalInvoiceIssueEvidence" ("DraftInvoiceId");
            CREATE UNIQUE INDEX "UX_BM_FinalIssueEvidence_Contract_Period"
                ON "BuildingManagementFinalInvoiceIssueEvidence"
                ("ContractId", "BillingYear", "BillingMonth");
            CREATE UNIQUE INDEX "UX_BM_FinalIssueEvidence_Room_Period"
                ON "BuildingManagementFinalInvoiceIssueEvidence"
                ("RoomId", "BillingYear", "BillingMonth");

            -- Immutable prepared evidence: even a matching SHA is not a
            -- financial approval. Database owners can bypass triggers;
            -- deployment would additionally require restricted runtime roles.
            CREATE FUNCTION "BM_RejectFinalIssueEvidenceMutation"()
            RETURNS trigger LANGUAGE plpgsql AS $evidence_guard$
            BEGIN
                RAISE EXCEPTION 'Prepared final invoice evidence is immutable'
                    USING ERRCODE = '23514';
            END;
            $evidence_guard$;
            CREATE TRIGGER "TR_BM_FinalIssueEvidence_Row"
                BEFORE UPDATE OR DELETE
                ON "BuildingManagementFinalInvoiceIssueEvidence"
                FOR EACH ROW EXECUTE FUNCTION "BM_RejectFinalIssueEvidenceMutation"();
            CREATE TRIGGER "TR_BM_FinalIssueEvidence_Truncate"
                BEFORE TRUNCATE ON "BuildingManagementFinalInvoiceIssueEvidence"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectFinalIssueEvidenceMutation"();

            CREATE TABLE "BuildingManagementInvoiceIssuanceReviews" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "StagedEvidenceId" uuid NOT NULL,
                "DraftInvoiceId" uuid NOT NULL,
                "MoveOutRequestId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                "SubmittedByUserId" uuid NOT NULL,
                "SubmittedAtUtc" timestamptz NOT NULL,
                "SubmissionNote" varchar(500) NULL,
                "Status" integer NOT NULL,
                "ReviewedByUserId" uuid NULL,
                "ReviewedAtUtc" timestamptz NULL,
                "ReviewReason" varchar(500) NULL,
                CONSTRAINT "FK_BM_IssueReview_Evidence"
                    FOREIGN KEY ("StagedEvidenceId")
                    REFERENCES "BuildingManagementFinalInvoiceIssueEvidence" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_IssueReview_Status"
                    CHECK ("Status" IN (1,2,3)),
                CONSTRAINT "CK_BM_IssueReview_Reason"
                    CHECK (("Status" = 1 AND "ReviewReason" IS NULL
                        AND "ReviewedByUserId" IS NULL AND "ReviewedAtUtc" IS NULL)
                      OR ("Status" IN (2,3) AND "ReviewedByUserId" IS NOT NULL
                        AND "ReviewedAtUtc" IS NOT NULL
                        AND "ReviewReason" IS NOT NULL
                        AND length(trim("ReviewReason")) BETWEEN 8 AND 500)),
                CONSTRAINT "CK_BM_IssueReview_IndependentReviewer"
                    CHECK ("ReviewedByUserId" IS NULL OR
                           "ReviewedByUserId" <> "SubmittedByUserId"),
                CONSTRAINT "CK_BM_IssueReview_Hash"
                    CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                CONSTRAINT "CK_BM_IssueReview_Ids"
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "StagedEvidenceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "SubmittedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                CONSTRAINT "CK_BM_IssueReview_Time"
                    CHECK ("ReviewedAtUtc" IS NULL OR
                           "ReviewedAtUtc" >= "SubmittedAtUtc")
            );
            CREATE UNIQUE INDEX "UX_BM_IssueReview_Evidence"
                ON "BuildingManagementInvoiceIssuanceReviews" ("StagedEvidenceId");
            CREATE UNIQUE INDEX "UX_BM_IssueReview_Draft"
                ON "BuildingManagementInvoiceIssuanceReviews" ("DraftInvoiceId");
            CREATE INDEX "IX_BM_IssueReview_Contract_Draft"
                ON "BuildingManagementInvoiceIssuanceReviews"
                ("ContractId", "DraftInvoiceId");

            -- Database-side scope/time check and exactly one decision
            -- transition. No HTTP writer is attached to this schema.
            CREATE FUNCTION "BM_GuardFinalInvoiceReview"()
            RETURNS trigger LANGUAGE plpgsql AS $review_guard$
            DECLARE source_record record;
            BEGIN
                IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                    RAISE EXCEPTION 'Final invoice review history cannot be deleted'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO source_record
                  FROM "BuildingManagementFinalInvoiceIssueEvidence"
                 WHERE "Id" = NEW."StagedEvidenceId";
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'Review source evidence does not exist'
                        USING ERRCODE = '23514';
                END IF;
                IF NEW."DraftInvoiceId" IS DISTINCT FROM source_record."DraftInvoiceId"
                   OR NEW."MoveOutRequestId" IS DISTINCT FROM source_record."MoveOutRequestId"
                   OR NEW."ContractId" IS DISTINCT FROM source_record."ContractId"
                   OR NEW."RoomId" IS DISTINCT FROM source_record."RoomId"
                   OR NEW."EvidenceSha256" IS DISTINCT FROM source_record."EvidenceSha256"
                   OR NEW."SubmittedAtUtc" < source_record."PreparedAtUtc" THEN
                    RAISE EXCEPTION 'Review must match the prepared evidence and chronology'
                        USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW."Status" <> 1 OR NEW."ReviewedByUserId" IS NOT NULL
                       OR NEW."ReviewedAtUtc" IS NOT NULL
                       OR NEW."ReviewReason" IS NOT NULL THEN
                        RAISE EXCEPTION 'Review must begin submitted and undecided'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;
                IF OLD."Status" <> 1 OR NEW."Status" NOT IN (2,3)
                   OR NEW."Id" IS DISTINCT FROM OLD."Id"
                   OR NEW."StagedEvidenceId" IS DISTINCT FROM OLD."StagedEvidenceId"
                   OR NEW."DraftInvoiceId" IS DISTINCT FROM OLD."DraftInvoiceId"
                   OR NEW."MoveOutRequestId" IS DISTINCT FROM OLD."MoveOutRequestId"
                   OR NEW."ContractId" IS DISTINCT FROM OLD."ContractId"
                   OR NEW."RoomId" IS DISTINCT FROM OLD."RoomId"
                   OR NEW."BuildingId" IS DISTINCT FROM OLD."BuildingId"
                   OR NEW."EvidenceSha256" IS DISTINCT FROM OLD."EvidenceSha256"
                   OR NEW."SubmittedByUserId" IS DISTINCT FROM OLD."SubmittedByUserId"
                   OR NEW."SubmittedAtUtc" IS DISTINCT FROM OLD."SubmittedAtUtc"
                   OR NEW."SubmissionNote" IS DISTINCT FROM OLD."SubmissionNote"
                   OR NEW."ReviewedByUserId" IS NULL
                   OR NEW."ReviewedByUserId" = OLD."SubmittedByUserId"
                   OR NEW."ReviewedAtUtc" IS NULL
                   OR NEW."ReviewedAtUtc" < OLD."SubmittedAtUtc"
                   OR NEW."ReviewReason" IS NULL
                   OR length(trim(NEW."ReviewReason")) NOT BETWEEN 8 AND 500 THEN
                    RAISE EXCEPTION 'Only one independent review decision is allowed'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $review_guard$;

            CREATE TRIGGER "TR_BM_IssueReview_DecisionOnly"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementInvoiceIssuanceReviews"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardFinalInvoiceReview"();
            CREATE TRIGGER "TR_BM_IssueReview_Truncate"
                BEFORE TRUNCATE ON "BuildingManagementInvoiceIssuanceReviews"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardFinalInvoiceReview"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Acquire both locks BEFORE checking for evidence or dropping
            -- either table. EF migrations run this in a transaction.
            LOCK TABLE "BuildingManagementInvoiceIssuanceReviews",
                       "BuildingManagementFinalInvoiceIssueEvidence"
                IN ACCESS EXCLUSIVE MODE;
            DO $invoice_evidence_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementInvoiceIssuanceReviews" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM "BuildingManagementFinalInvoiceIssueEvidence" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Final invoice evidence/review history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $invoice_evidence_down$;
            DROP TABLE "BuildingManagementInvoiceIssuanceReviews";
            DROP TABLE "BuildingManagementFinalInvoiceIssueEvidence";
            DROP FUNCTION "BM_GuardFinalInvoiceReview"();
            DROP FUNCTION "BM_RejectFinalIssueEvidenceMutation"();
            """);
    }
}
