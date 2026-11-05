using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Isolated D04 evidence-first H4 settlement journal. The original H4
/// obligation is immutable. This migration never moves bank funds and never
/// changes an issued invoice; financial recognition requires reviewed sources.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261105150000_Phase2HD04H4SettlementJournal")]
public sealed class Phase2HD04H4SettlementJournal : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeH4SettlementReviews"(
          "Id" uuid PRIMARY KEY,
          "ObligationId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeH4Obligations"("Id") ON DELETE RESTRICT,
          "Kind" integer NOT NULL,
          "PaymentId" uuid NULL REFERENCES
            "BuildingManagementPayments"("Id") ON DELETE RESTRICT,
          "AmountVnd" numeric(18,0) NOT NULL,
          "BankTransactionReference" varchar(128) NOT NULL,
          "BankStatementEvidenceReference" varchar(500) NOT NULL,
          "PaidAtUtc" timestamptz NOT NULL,
          "ReviewedByUserId" uuid NOT NULL,
          "ReviewedAtUtc" timestamptz NOT NULL,
          "SignedReviewReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_D04_H4ReviewShape" CHECK (
            "Kind" IN (1,3)
            AND "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "AmountVnd">0 AND "AmountVnd"=trunc("AmountVnd")
            AND length(btrim("BankTransactionReference")) BETWEEN 12 AND 128
            AND length(btrim("BankStatementEvidenceReference")) BETWEEN 12 AND 500
            AND length(btrim("SignedReviewReference")) BETWEEN 12 AND 500
            AND "ReviewedAtUtc">="PaidAtUtc"
            AND "ReviewedByUserId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND (("Kind"=1 AND "PaymentId" IS NOT NULL)
              OR ("Kind"=3 AND "PaymentId" IS NULL))
          )
        );
        CREATE UNIQUE INDEX "UX_BM_D04_H4Review_Payment"
          ON "BuildingManagementRoomChangeH4SettlementReviews"("PaymentId")
          WHERE "PaymentId" IS NOT NULL;
        CREATE UNIQUE INDEX "UX_BM_D04_H4Review_BankReference"
          ON "BuildingManagementRoomChangeH4SettlementReviews"
          ("BankTransactionReference");

        CREATE TABLE "BuildingManagementRoomChangeH4SettlementPostings"(
          "Id" uuid PRIMARY KEY,
          "ObligationId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeH4Obligations"("Id") ON DELETE RESTRICT,
          "Kind" integer NOT NULL,
          "ReviewId" uuid NULL REFERENCES
            "BuildingManagementRoomChangeH4SettlementReviews"("Id") ON DELETE RESTRICT,
          "IssuedInvoiceId" uuid NULL REFERENCES
            "BuildingManagementMonthlyIssuedInvoiceLedger"("Id") ON DELETE RESTRICT,
          "AmountVnd" numeric(18,0) NOT NULL,
          "PostedByOwnerId" uuid NOT NULL,
          "PostedAtUtc" timestamptz NOT NULL,
          "SourceReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_D04_H4PostingShape" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "Kind" IN (1,2,3)
            AND "AmountVnd">0 AND "AmountVnd"=trunc("AmountVnd")
            AND "PostedByOwnerId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("SourceReference")) BETWEEN 12 AND 500
            AND ((("Kind"=1 OR "Kind"=3)
                    AND "ReviewId" IS NOT NULL AND "IssuedInvoiceId" IS NULL)
              OR ("Kind"=2 AND "ReviewId" IS NULL
                  AND "IssuedInvoiceId" IS NOT NULL))
          )
        );
        CREATE UNIQUE INDEX "UX_BM_D04_H4Posting_Review"
          ON "BuildingManagementRoomChangeH4SettlementPostings"("ReviewId")
          WHERE "ReviewId" IS NOT NULL;
        CREATE UNIQUE INDEX "UX_BM_D04_H4Posting_Invoice"
          ON "BuildingManagementRoomChangeH4SettlementPostings"("IssuedInvoiceId")
          WHERE "IssuedInvoiceId" IS NOT NULL;
        CREATE UNIQUE INDEX "UX_BM_D04_H4Posting_Reference"
          ON "BuildingManagementRoomChangeH4SettlementPostings"("SourceReference");
        CREATE UNIQUE INDEX "UX_BM_D04_H4Posting_OneNonTopup"
          ON "BuildingManagementRoomChangeH4SettlementPostings"("ObligationId")
          WHERE "Kind" IN (2,3);

        CREATE FUNCTION "BM_D04GuardH4SettlementReview"()
        RETURNS trigger LANGUAGE plpgsql AS $review$
        DECLARE obligation record; payment record;
        BEGIN
          IF TG_OP<>'INSERT' OR
             current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'D04 review is append-only SERIALIZABLE'
              USING ERRCODE='23514';
          END IF;
          SELECT * INTO obligation
            FROM "BuildingManagementRoomChangeH4Obligations"
           WHERE "Id"=NEW."ObligationId" FOR UPDATE;
          IF obligation."Id" IS NULL
             OR obligation."Kind" IS DISTINCT FROM NEW."Kind"
             OR NEW."ReviewedByUserId"=obligation."RecordedByOwnerId"
             OR NEW."PaidAtUtc"<obligation."RecordedAtUtc"
             OR NEW."AmountVnd">obligation."AmountVnd"
             OR NEW."BankTransactionReference"=NEW."BankStatementEvidenceReference"
             OR NEW."BankTransactionReference"=NEW."SignedReviewReference"
             OR NEW."BankStatementEvidenceReference"=NEW."SignedReviewReference"
          THEN
            RAISE EXCEPTION 'D04 independent reviewed evidence mismatch'
              USING ERRCODE='23514';
          END IF;
          IF NEW."Kind"=1 THEN
            SELECT * INTO payment FROM "BuildingManagementPayments"
             WHERE "Id"=NEW."PaymentId" FOR SHARE;
            IF payment."Id" IS NULL
               OR payment."ContractId" IS DISTINCT FROM obligation."TargetContractId"
               OR payment."Purpose"<>2 OR payment."Status"<>2
               OR payment."Method"<>2 OR payment."Channel"<>1
               OR payment."IsDeleted" IS DISTINCT FROM FALSE
               OR payment."Amount" IS DISTINCT FROM NEW."AmountVnd"
               OR payment."PaidAt" IS DISTINCT FROM NEW."PaidAtUtc"
               OR payment."TransactionReference" IS DISTINCT FROM
                  NEW."BankTransactionReference"
               OR coalesce(payment."Notes",'') NOT LIKE 'BANK-PROOF: %'
               OR NEW."ReviewedByUserId"=payment."CreatorId"
               OR EXISTS (
                 SELECT 1 FROM "BuildingManagementDepositSettlements" d
                  WHERE d."PaymentId"=NEW."PaymentId"
                    AND d."Status"=2
               )
            THEN
              RAISE EXCEPTION 'Top-up review requires exact completed bank-backed receipt'
                USING ERRCODE='23514';
            END IF;
          END IF;
          RETURN NEW;
        END;
        $review$;
        CREATE TRIGGER "TR_BM_D04_H4ReviewImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeH4SettlementReviews"
          FOR EACH ROW EXECUTE FUNCTION "BM_D04GuardH4SettlementReview"();

        CREATE FUNCTION "BM_D04GuardH4SettlementPosting"()
        RETURNS trigger LANGUAGE plpgsql AS $posting$
        DECLARE obligation record; review record; invoice record;
                receivable record; candidate record; prior numeric;
                received numeric; signed_delta numeric; month_start date;
        BEGIN
          IF TG_OP<>'INSERT' OR
             current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'D04 postings must be append-only SERIALIZABLE'
              USING ERRCODE='23514';
          END IF;
          SELECT * INTO obligation
            FROM "BuildingManagementRoomChangeH4Obligations"
           WHERE "Id"=NEW."ObligationId" FOR UPDATE;
          SELECT COALESCE(sum("AmountVnd"),0) INTO prior
            FROM "BuildingManagementRoomChangeH4SettlementPostings"
           WHERE "ObligationId"=NEW."ObligationId";
          IF obligation."Id" IS NULL
             OR obligation."Kind" IS DISTINCT FROM NEW."Kind"
             OR NEW."PostedByOwnerId" IS DISTINCT FROM obligation."RecordedByOwnerId"
             OR NEW."PostedAtUtc"<obligation."RecordedAtUtc"
             OR prior+NEW."AmountVnd">obligation."AmountVnd"
          THEN
            RAISE EXCEPTION 'D04 posting cannot exceed original signed H4 obligation'
              USING ERRCODE='23514';
          END IF;
          IF NEW."Kind" IN (1,3) THEN
            SELECT * INTO review
              FROM "BuildingManagementRoomChangeH4SettlementReviews"
             WHERE "Id"=NEW."ReviewId" FOR SHARE;
            IF review."Id" IS NULL
               OR review."ObligationId" IS DISTINCT FROM obligation."Id"
               OR review."Kind" IS DISTINCT FROM NEW."Kind"
               OR review."AmountVnd" IS DISTINCT FROM NEW."AmountVnd"
               OR review."ReviewedAtUtc">NEW."PostedAtUtc"
               OR NEW."SourceReference" IS DISTINCT FROM
                  review."BankTransactionReference"
               OR review."ReviewedByUserId"=NEW."PostedByOwnerId"
            THEN
              RAISE EXCEPTION 'D04 posting must match independently reviewed source'
                USING ERRCODE='23514';
            END IF;
            IF NEW."Kind"=3 AND
               (prior<>0 OR NEW."AmountVnd"<>obligation."AmountVnd") THEN
              RAISE EXCEPTION 'Bank refund may only settle exactly once'
                USING ERRCODE='23514';
            END IF;
          ELSE
            SELECT * INTO invoice
              FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
             WHERE "Id"=NEW."IssuedInvoiceId" FOR SHARE;
            SELECT * INTO candidate
              FROM "BuildingManagementMonthlyInvoiceCandidates"
             WHERE "Id"=invoice."CandidateId" FOR SHARE;
            SELECT * INTO receivable
              FROM "BuildingManagementOfficialInvoiceReceivables"
             WHERE "IssuedInvoiceId"=NEW."IssuedInvoiceId"
               AND "Kind"=1 FOR UPDATE;
            SELECT COALESCE(sum("Amount"),0) INTO received
              FROM "BuildingManagementOfficialInvoiceReceiptReviews"
             WHERE "ReceivablePostingId"=receivable."Id" AND "Accepted"=TRUE;
            SELECT COALESCE(sum("ApprovedSignedDeltaAmount"),0) INTO signed_delta
              FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
             WHERE "IssuedInvoiceId"=NEW."IssuedInvoiceId" AND "Accepted"=TRUE;
            month_start:=(date_trunc('month',
              (SELECT "RequestedDate"::timestamp
                 FROM "BuildingManagementRoomChangeRequests"
                WHERE "Id"=obligation."RequestId")) + interval '1 month')::date;
            IF invoice."Id" IS NULL OR receivable."Id" IS NULL
               OR candidate."Id" IS NULL
               OR candidate."ContractId" IS DISTINCT FROM invoice."ContractId"
               OR candidate."RentAmount"-received<NEW."AmountVnd"
               OR invoice."ContractId" IS DISTINCT FROM obligation."TargetContractId"
               OR receivable."ContractId" IS DISTINCT FROM invoice."ContractId"
               OR invoice."BuildingId" IS DISTINCT FROM receivable."BuildingId"
               OR make_date(invoice."BillingYear",invoice."BillingMonth",1)
                  IS DISTINCT FROM month_start
               OR NEW."SourceReference" IS DISTINCT FROM invoice."InvoiceNumber"
               OR prior<>0 OR NEW."AmountVnd"<>obligation."AmountVnd"
               OR receivable."OriginalAmount"+signed_delta-received<NEW."AmountVnd"
               OR NEW."PostedAtUtc"<invoice."IssuedAtUtc"
            THEN
              RAISE EXCEPTION 'Next-rent credit requires real issued target rent and sufficient debt'
                USING ERRCODE='23514';
            END IF;
          END IF;
          RETURN NEW;
        END;
        $posting$;
        CREATE TRIGGER "TR_BM_D04_H4PostingImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeH4SettlementPostings"
          FOR EACH ROW EXECUTE FUNCTION "BM_D04GuardH4SettlementPosting"();

        CREATE FUNCTION "BM_D04NoTruncateH4Settlement"()
        RETURNS trigger LANGUAGE plpgsql AS $deny$
        BEGIN
          RAISE EXCEPTION 'D04 evidence and postings cannot be truncated'
            USING ERRCODE='23514';
        END;
        $deny$;
        CREATE TRIGGER "TR_BM_D04_H4ReviewNoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomChangeH4SettlementReviews"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_D04NoTruncateH4Settlement"();
        CREATE TRIGGER "TR_BM_D04_H4PostingNoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomChangeH4SettlementPostings"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_D04NoTruncateH4Settlement"();

        -- A bank receipt posted AFTER a next-rent credit must not overpay
        -- the same immutable official receivable. Original receipt guards
        -- did not know about D04's new journal, so enforce both sources.
        CREATE FUNCTION "BM_D04GuardReceiptsAfterRentCredit"()
        RETURNS trigger LANGUAGE plpgsql AS $credit_recheck$
        DECLARE due record; receipts numeric; credits numeric; delta numeric;
        BEGIN
          IF NEW."Accepted" IS DISTINCT FROM TRUE THEN RETURN NEW; END IF;
          SELECT * INTO due
            FROM "BuildingManagementOfficialInvoiceReceivables"
           WHERE "Id"=NEW."ReceivablePostingId" AND "Kind"=1 FOR UPDATE;
          IF due."Id" IS NULL THEN RETURN NEW; END IF;
          SELECT COALESCE(sum(p."AmountVnd"),0) INTO credits
            FROM "BuildingManagementRoomChangeH4SettlementPostings" p
           WHERE p."Kind"=2 AND p."IssuedInvoiceId"=due."IssuedInvoiceId";
          SELECT COALESCE(sum(r."Amount"),0) INTO receipts
            FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
           WHERE r."ReceivablePostingId"=due."Id" AND r."Accepted"=TRUE;
          SELECT COALESCE(sum(a."ApprovedSignedDeltaAmount"),0) INTO delta
            FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" a
           WHERE a."IssuedInvoiceId"=due."IssuedInvoiceId"
             AND a."Accepted"=TRUE;
          IF receipts+credits>due."OriginalAmount"+delta THEN
            RAISE EXCEPTION 'D04 rent credit plus bank receipts exceed issued debt'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $credit_recheck$;
        CREATE TRIGGER "TR_BM_D04_OfficialReceiptCreditCap"
          AFTER INSERT ON "BuildingManagementOfficialInvoiceReceiptReviews"
          FOR EACH ROW EXECUTE FUNCTION "BM_D04GuardReceiptsAfterRentCredit"();

        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeH4SettlementPostings",
                   "BuildingManagementRoomChangeH4SettlementReviews"
          IN ACCESS EXCLUSIVE MODE;
        DO $revert$
        BEGIN
          IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeH4SettlementPostings")
             OR EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeH4SettlementReviews")
          THEN
            RAISE EXCEPTION 'Cannot drop D04 signed cash/credit/refund history'
              USING ERRCODE='23514';
          END IF;
        END;
        $revert$;
        DROP TRIGGER "TR_BM_D04_OfficialReceiptCreditCap"
          ON "BuildingManagementOfficialInvoiceReceiptReviews";
        DROP FUNCTION "BM_D04GuardReceiptsAfterRentCredit"();
        DROP TRIGGER "TR_BM_D04_H4PostingNoTruncate"
          ON "BuildingManagementRoomChangeH4SettlementPostings";
        DROP TRIGGER "TR_BM_D04_H4ReviewNoTruncate"
          ON "BuildingManagementRoomChangeH4SettlementReviews";
        DROP FUNCTION "BM_D04NoTruncateH4Settlement"();
        DROP TRIGGER "TR_BM_D04_H4PostingImmutable"
          ON "BuildingManagementRoomChangeH4SettlementPostings";
        DROP FUNCTION "BM_D04GuardH4SettlementPosting"();
        DROP TRIGGER "TR_BM_D04_H4ReviewImmutable"
          ON "BuildingManagementRoomChangeH4SettlementReviews";
        DROP FUNCTION "BM_D04GuardH4SettlementReview"();
        DROP TABLE "BuildingManagementRoomChangeH4SettlementPostings";
        DROP TABLE "BuildingManagementRoomChangeH4SettlementReviews";
        """);
}
