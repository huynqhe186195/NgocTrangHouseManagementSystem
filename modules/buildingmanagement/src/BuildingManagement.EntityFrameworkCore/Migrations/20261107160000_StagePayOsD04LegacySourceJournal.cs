using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// SOURCE ONLY / DRAFT. Dedicated BuildingManagement-side payOS D04 audit
/// schema: independently verified bank evidence, distinct Owner approval,
/// one canonical legacy Payment/DepositSettlement receipt and one outbox.
/// Do NOT apply to production. No HTTP writer or webhook uses these tables.
/// Deliberately absent from the legacy EF model snapshot pending acceptance.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261107160000_StagePayOsD04LegacySourceJournal")]
public sealed class StagePayOsD04LegacySourceJournal : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- This migration creates NO Payment and never changes cash balances.
            -- Independent evidence is not equivalent to a payOS-signed event.
            CREATE TABLE "BuildingManagementPayOsD04BankReviews" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PayOsIntentId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "PaymentId" uuid NOT NULL
                    REFERENCES "BuildingManagementPayments"("Id") ON DELETE RESTRICT,
                "AmountVnd" bigint NOT NULL CHECK ("AmountVnd" > 0),
                "BankReference" varchar(128) NOT NULL,
                "DocumentSha256" varchar(64) NOT NULL
                    CHECK ("DocumentSha256" ~ '^[0-9a-f]{64}$'),
                "ExternalBankProofReference" varchar(256) NOT NULL
                    CHECK (length(btrim("ExternalBankProofReference")) >= 8),
                "ReviewerId" uuid NOT NULL,
                "VerifiedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_PayOsD04Review_NotBlank"
                    CHECK (length(btrim("BankReference")) BETWEEN 1 AND 128),
                CONSTRAINT "UX_PayOsD04Review_Provenance"
                    UNIQUE ("Id","ReviewerId","DocumentSha256")
            );
            CREATE UNIQUE INDEX "UX_PayOsD04Review_Intent"
                ON "BuildingManagementPayOsD04BankReviews"("PayOsIntentId");
            CREATE UNIQUE INDEX "UX_PayOsD04Review_Payment"
                ON "BuildingManagementPayOsD04BankReviews"("PaymentId");
            CREATE UNIQUE INDEX "UX_PayOsD04Review_BankReference"
                ON "BuildingManagementPayOsD04BankReviews"("BankReference");

            CREATE TABLE "BuildingManagementPayOsD04OwnerApprovals" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "BankReviewId" uuid NOT NULL,
                "ReviewerId" uuid NOT NULL,
                "DocumentSha256" varchar(64) NOT NULL,
                "OwnerApproverId" uuid NOT NULL,
                "ApprovedAtUtc" timestamptz NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_PayOsD04Approval_Distinct"
                    CHECK ("OwnerApproverId" <> "ReviewerId"
                      AND "ExpiresAtUtc" > "ApprovedAtUtc"),
                CONSTRAINT "FK_PayOsD04Approval_ExactReview"
                    FOREIGN KEY ("BankReviewId","ReviewerId","DocumentSha256")
                    REFERENCES "BuildingManagementPayOsD04BankReviews"
                       ("Id","ReviewerId","DocumentSha256") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "UX_PayOsD04Approval_Review"
                ON "BuildingManagementPayOsD04OwnerApprovals"("BankReviewId");

            CREATE TABLE "BuildingManagementPayOsD04ReceiptJournal" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PayOsIntentId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "ContractId" uuid NOT NULL
                    REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
                "PaymentId" uuid NOT NULL
                    REFERENCES "BuildingManagementPayments"("Id") ON DELETE RESTRICT,
                "DepositSettlementId" uuid NOT NULL
                    REFERENCES "BuildingManagementDepositSettlements"("Id") ON DELETE RESTRICT,
                "BankReviewId" uuid NOT NULL
                    REFERENCES "BuildingManagementPayOsD04BankReviews"("Id") ON DELETE RESTRICT,
                "OwnerApprovalId" uuid NOT NULL
                    REFERENCES "BuildingManagementPayOsD04OwnerApprovals"("Id") ON DELETE RESTRICT,
                "SourceKey" varchar(80) NOT NULL,
                "BankReference" varchar(128) NOT NULL,
                "AmountVnd" bigint NOT NULL CHECK ("AmountVnd" > 0),
                "DocumentSha256" varchar(64) NOT NULL
                    CHECK ("DocumentSha256" ~ '^[0-9a-f]{64}$'),
                "ReviewerId" uuid NOT NULL,
                "OwnerApproverId" uuid NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_PayOsD04Journal_SourceKey"
                    CHECK ("SourceKey" = 'PAYOS:D04:' ||
                        replace("PayOsIntentId"::text, '-', '')),
                CONSTRAINT "CK_PayOsD04Journal_DistinctActor"
                    CHECK ("ReviewerId" <> "OwnerApproverId"),
                CONSTRAINT "UX_PayOsD04Journal_ReceiptSource"
                    UNIQUE ("Id","SourceKey")
            );
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_Intent"
                ON "BuildingManagementPayOsD04ReceiptJournal"("PayOsIntentId");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_Payment"
                ON "BuildingManagementPayOsD04ReceiptJournal"("PaymentId");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_Settlement"
                ON "BuildingManagementPayOsD04ReceiptJournal"("DepositSettlementId");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_SourceKey"
                ON "BuildingManagementPayOsD04ReceiptJournal"("SourceKey");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_BankReference"
                ON "BuildingManagementPayOsD04ReceiptJournal"("BankReference");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_Review"
                ON "BuildingManagementPayOsD04ReceiptJournal"("BankReviewId");
            CREATE UNIQUE INDEX "UX_PayOsD04Journal_Approval"
                ON "BuildingManagementPayOsD04ReceiptJournal"("OwnerApprovalId");

            CREATE TABLE "BuildingManagementPayOsD04Outbox" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ReceiptId" uuid NOT NULL,
                "SourceKey" varchar(80) NOT NULL,
                "EventType" varchar(64) NOT NULL
                    CHECK ("EventType" = 'D04PayOsReceiptCommitted'),
                "CreatedAtUtc" timestamptz NOT NULL,
                "AcknowledgedAtUtc" timestamptz NULL,
                "AttemptCount" integer NOT NULL DEFAULT 0 CHECK ("AttemptCount" >= 0),
                CONSTRAINT "CK_PayOsD04Outbox_AckTime"
                    CHECK ("AcknowledgedAtUtc" IS NULL OR
                           "AcknowledgedAtUtc" >= "CreatedAtUtc"),
                CONSTRAINT "FK_PayOsD04Outbox_ReceiptSource"
                    FOREIGN KEY ("ReceiptId","SourceKey")
                    REFERENCES "BuildingManagementPayOsD04ReceiptJournal"
                       ("Id","SourceKey") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "UX_PayOsD04Outbox_Receipt"
                ON "BuildingManagementPayOsD04Outbox"("ReceiptId");
            CREATE UNIQUE INDEX "UX_PayOsD04Outbox_SourceKey"
                ON "BuildingManagementPayOsD04Outbox"("SourceKey");

            -- Audit rows are insert-only even for ordinary DB writers; only
            -- the outbox acknowledgment/attempt metadata is mutable.
            CREATE FUNCTION "BM_PayOsD04RejectAuditMutation"()
            RETURNS trigger LANGUAGE plpgsql AS $d04_immutable$
            BEGIN
                RAISE EXCEPTION 'D04 independent evidence, Owner approval and receipt are immutable'
                    USING ERRCODE = '23514';
            END;
            $d04_immutable$;
            CREATE TRIGGER "TR_PayOsD04BankReview_Immutable" BEFORE UPDATE OR DELETE
                ON "BuildingManagementPayOsD04BankReviews" FOR EACH ROW
                EXECUTE FUNCTION "BM_PayOsD04RejectAuditMutation"();
            CREATE TRIGGER "TR_PayOsD04OwnerApproval_Immutable" BEFORE UPDATE OR DELETE
                ON "BuildingManagementPayOsD04OwnerApprovals" FOR EACH ROW
                EXECUTE FUNCTION "BM_PayOsD04RejectAuditMutation"();
            CREATE TRIGGER "TR_PayOsD04Receipt_Immutable" BEFORE UPDATE OR DELETE
                ON "BuildingManagementPayOsD04ReceiptJournal" FOR EACH ROW
                EXECUTE FUNCTION "BM_PayOsD04RejectAuditMutation"();

            CREATE FUNCTION "BM_PayOsD04GuardOutboxMutation"()
            RETURNS trigger LANGUAGE plpgsql AS $d04_outbox$
            BEGIN
                IF TG_OP = 'DELETE' OR
                   NEW."Id" IS DISTINCT FROM OLD."Id" OR
                   NEW."ReceiptId" IS DISTINCT FROM OLD."ReceiptId" OR
                   NEW."SourceKey" IS DISTINCT FROM OLD."SourceKey" OR
                   NEW."EventType" IS DISTINCT FROM OLD."EventType" OR
                   NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR
                   NEW."AttemptCount" < OLD."AttemptCount" OR
                   NEW."AcknowledgedAtUtc" IS DISTINCT FROM OLD."AcknowledgedAtUtc"
                   AND OLD."AcknowledgedAtUtc" IS NOT NULL
                THEN
                    RAISE EXCEPTION 'D04 outbox source/ack is immutable'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $d04_outbox$;
            CREATE TRIGGER "TR_PayOsD04Outbox_Guard" BEFORE UPDATE OR DELETE
                ON "BuildingManagementPayOsD04Outbox" FOR EACH ROW
                EXECUTE FUNCTION "BM_PayOsD04GuardOutboxMutation"();

            CREATE FUNCTION "BM_PayOsD04RejectTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $d04_truncate$
            BEGIN
                RAISE EXCEPTION 'D04 financial audit cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $d04_truncate$;
            CREATE TRIGGER "TR_PayOsD04Review_NoTruncate" BEFORE TRUNCATE
                ON "BuildingManagementPayOsD04BankReviews" FOR EACH STATEMENT
                EXECUTE FUNCTION "BM_PayOsD04RejectTruncate"();
            CREATE TRIGGER "TR_PayOsD04Approval_NoTruncate" BEFORE TRUNCATE
                ON "BuildingManagementPayOsD04OwnerApprovals" FOR EACH STATEMENT
                EXECUTE FUNCTION "BM_PayOsD04RejectTruncate"();
            CREATE TRIGGER "TR_PayOsD04Receipt_NoTruncate" BEFORE TRUNCATE
                ON "BuildingManagementPayOsD04ReceiptJournal" FOR EACH STATEMENT
                EXECUTE FUNCTION "BM_PayOsD04RejectTruncate"();
            CREATE TRIGGER "TR_PayOsD04Outbox_NoTruncate" BEFORE TRUNCATE
                ON "BuildingManagementPayOsD04Outbox" FOR EACH STATEMENT
                EXECUTE FUNCTION "BM_PayOsD04RejectTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        // Refunds/receipts cannot be erased to "roll back" signed financial
        // history. Reject downgrade once any review/approval/receipt exists.
        builder.Sql("""
            DO $d04_down$
            BEGIN
              IF EXISTS (SELECT 1 FROM "BuildingManagementPayOsD04BankReviews")
                  OR EXISTS (SELECT 1 FROM "BuildingManagementPayOsD04OwnerApprovals")
                  OR EXISTS (SELECT 1 FROM "BuildingManagementPayOsD04ReceiptJournal")
                  OR EXISTS (SELECT 1 FROM "BuildingManagementPayOsD04Outbox")
              THEN
                  RAISE EXCEPTION 'D04 audit history exists: downgrade forbidden'
                      USING ERRCODE = '23514';
              END IF;
            END;
            $d04_down$;
            DROP TABLE "BuildingManagementPayOsD04Outbox";
            DROP TABLE "BuildingManagementPayOsD04ReceiptJournal";
            DROP TABLE "BuildingManagementPayOsD04OwnerApprovals";
            DROP TABLE "BuildingManagementPayOsD04BankReviews";
            DROP FUNCTION "BM_PayOsD04GuardOutboxMutation"();
            DROP FUNCTION "BM_PayOsD04RejectAuditMutation"();
            DROP FUNCTION "BM_PayOsD04RejectTruncate"();
            """);
    }
}
