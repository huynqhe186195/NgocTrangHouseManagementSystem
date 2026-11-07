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
// SAFETY: This is deliberately NOT decorated with DbContextAttribute or
// MigrationAttribute. EF Core must NOT discover/apply it in the canonical
// BuildingManagementDbContext Migrate() chain. The isolated PostgreSQL
// acceptance test may invoke UpOperations explicitly after creating disposable
// stub tables. Before production, replace with a separately approved,
// fully rehearsed migration that passes the complete chain.
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
                "OrderCode" bigint NOT NULL CHECK ("OrderCode" > 0),
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
                ON "BuildingManagementPayOsD04BankReviews"((lower(btrim("BankReference"))));

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
                "OrderCode" bigint NOT NULL CHECK ("OrderCode" > 0),
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
                ON "BuildingManagementPayOsD04ReceiptJournal"((lower(btrim("BankReference"))));
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

            -- A direct SQL caller cannot claim a journal by supplying only
            -- trusted-looking IDs. The actual completed Payment and completed
            -- allocation must agree with the durable reviewed proof.
            -- SERIALIZABLE is mandatory to exclude racing deposit writers.
            CREATE FUNCTION "BM_PayOsD04ValidateReceipt"()
            RETURNS trigger LANGUAGE plpgsql AS $d04_receipt$
            DECLARE bank record;
            DECLARE approval record;
            DECLARE paid record;
            DECLARE source_contract record;
            DECLARE allocation record;
            DECLARE matched_global_claim boolean;
            DECLARE contract_building uuid;
            DECLARE primary_tenant_is_current boolean;
            BEGIN
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'D04 financial receipt requires SERIALIZABLE'
                    USING ERRCODE='23514';
              END IF;
              SELECT * INTO bank FROM "BuildingManagementPayOsD04BankReviews"
               WHERE "Id"=NEW."BankReviewId" FOR SHARE;
              SELECT * INTO approval FROM "BuildingManagementPayOsD04OwnerApprovals"
               WHERE "Id"=NEW."OwnerApprovalId" FOR SHARE;
              SELECT * INTO paid FROM "BuildingManagementPayments"
               WHERE "Id"=NEW."PaymentId" FOR SHARE;
              SELECT * INTO source_contract FROM "BuildingManagementContracts"
               WHERE "Id"=NEW."ContractId" FOR SHARE;
              -- Scope must come from canonical Contract -> Room -> Floor.
              -- Never trust a caller-supplied BuildingId in an approval row.
              SELECT f."BuildingId" INTO contract_building
                FROM "BuildingManagementRooms" r
                JOIN "BuildingManagementFloors" f ON f."Id"=r."FloorId"
               WHERE r."Id"=source_contract."RoomId"
                 AND r."IsDeleted"=FALSE AND f."IsDeleted"=FALSE
               FOR SHARE OF r,f;
              SELECT * INTO allocation FROM "BuildingManagementDepositSettlements"
               WHERE "Id"=NEW."DepositSettlementId" FOR SHARE;
              -- Reuse the existing 2G.4 GLOBAL claim table (including refund
              -- references). Never introduce an independent conflicting
              -- notion of who owns this bank transaction reference.
              SELECT EXISTS (
                SELECT 1 FROM "BuildingManagementBankEvidenceClaims" c
                 WHERE c."NormalizedReference"=lower(btrim(NEW."BankReference"))
                   AND c."SourceKind"='Payment'
                   AND c."SourceId"=NEW."PaymentId"
              ) INTO matched_global_claim;
              SELECT EXISTS(
                SELECT 1 FROM "BuildingManagementContractTenants" ct
                 WHERE ct."ContractId"=NEW."ContractId"
                   AND ct."TenantId"=paid."TenantId"
                   AND ct."Role"=1 AND ct."IsDeleted"=FALSE
              ) INTO primary_tenant_is_current;

              IF bank."Id" IS NULL OR approval."Id" IS NULL OR
                 paid."Id" IS NULL OR source_contract."Id" IS NULL OR
                 allocation."Id" IS NULL OR NOT matched_global_claim OR
                 NOT primary_tenant_is_current OR
                 contract_building IS DISTINCT FROM NEW."BuildingId" OR
                 source_contract."IsDeleted" IS DISTINCT FROM FALSE OR
                 bank."PayOsIntentId" IS DISTINCT FROM NEW."PayOsIntentId" OR
                 bank."OrderCode" IS DISTINCT FROM NEW."OrderCode" OR
                 bank."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                 bank."PaymentId" IS DISTINCT FROM NEW."PaymentId" OR
                 bank."AmountVnd" IS DISTINCT FROM NEW."AmountVnd" OR
                 bank."BankReference" IS DISTINCT FROM NEW."BankReference" OR
                 bank."DocumentSha256" IS DISTINCT FROM NEW."DocumentSha256" OR
                 bank."ReviewerId" IS DISTINCT FROM NEW."ReviewerId" OR
                 approval."BankReviewId" IS DISTINCT FROM bank."Id" OR
                 approval."ReviewerId" IS DISTINCT FROM bank."ReviewerId" OR
                 approval."OwnerApproverId" IS DISTINCT FROM NEW."OwnerApproverId" OR
                 approval."DocumentSha256" IS DISTINCT FROM bank."DocumentSha256" OR
                 bank."VerifiedAtUtc" > approval."ApprovedAtUtc" OR
                 bank."VerifiedAtUtc" > NEW."CreatedAtUtc" OR
                 approval."ApprovedAtUtc" > NEW."CreatedAtUtc" OR
                 approval."ExpiresAtUtc" <= NEW."CreatedAtUtc" OR
                 paid."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                 source_contract."Status" NOT IN (2,5) OR
                 paid."Purpose" IS DISTINCT FROM 2 OR
                 paid."Method" IS DISTINCT FROM 2 OR
                 paid."Channel" IS DISTINCT FROM 2 OR
                 paid."Status" IS DISTINCT FROM 2 OR
                 paid."PaidAt" IS NULL OR
                 paid."DepositDisposition" IS DISTINCT FROM 1 OR
                 paid."IsDeleted" IS DISTINCT FROM FALSE OR
                 paid."Amount" IS DISTINCT FROM NEW."AmountVnd" OR
                 paid."TransactionReference" IS DISTINCT FROM NEW."BankReference" OR
                 allocation."Type" IS DISTINCT FROM 3 OR
                 allocation."Status" IS DISTINCT FROM 2 OR
                 allocation."PaymentId" IS DISTINCT FROM NEW."PaymentId" OR
                 allocation."TargetContractId" IS DISTINCT FROM NEW."ContractId" OR
                 allocation."Amount" IS DISTINCT FROM NEW."AmountVnd" THEN
                RAISE EXCEPTION 'D04 receipt lacks exact completed Payment/settlement and bank review approval'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $d04_receipt$;
            CREATE TRIGGER "TR_PayOsD04Receipt_VerifyLegacy"
                BEFORE INSERT ON "BuildingManagementPayOsD04ReceiptJournal"
                FOR EACH ROW EXECUTE FUNCTION "BM_PayOsD04ValidateReceipt"();

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
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'D04 outbox cannot be deleted'
                        USING ERRCODE = '23514';
                END IF;
                IF NEW."Id" IS DISTINCT FROM OLD."Id" OR
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
            DROP FUNCTION "BM_PayOsD04ValidateReceipt"();
            DROP FUNCTION "BM_PayOsD04GuardOutboxMutation"();
            DROP FUNCTION "BM_PayOsD04RejectAuditMutation"();
            DROP FUNCTION "BM_PayOsD04RejectTruncate"();
            """);
    }
}
