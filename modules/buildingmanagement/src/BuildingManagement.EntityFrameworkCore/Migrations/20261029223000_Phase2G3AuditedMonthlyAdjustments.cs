using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// 2G.3 source-only, authenticated staging audit for independent post-issue
/// credit/debit notes. Original issued evidence stays immutable. The
/// corrected amount does NOT become official debt or bank verification.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029223000_Phase2G3AuditedMonthlyAdjustments")]
public sealed class Phase2G3AuditedMonthlyAdjustments : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "IssuedInvoiceId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyIssuedInvoiceLedger" ("Id") ON DELETE RESTRICT,
                "ContractId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "InvoiceEvidenceSha256" varchar(64) NOT NULL,
                "SignedDeltaAmount" numeric(18,0) NOT NULL,
                "Reason" varchar(500) NOT NULL,
                "SourceEvidenceReference" varchar(200) NOT NULL,
                "ProposedByUserId" uuid NOT NULL,
                "ProposedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_BM_MonthlyAdjustment_Finance"
                  CHECK ("SignedDeltaAmount" <> 0
                    AND "SignedDeltaAmount" = trunc("SignedDeltaAmount")
                    AND length(btrim("Reason")) BETWEEN 12 AND 500
                    AND length(btrim("SourceEvidenceReference")) BETWEEN 8 AND 200)
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyAdjustment_Evidence"
              ON "BuildingManagementMonthlyInvoiceAdjustments"
                ("IssuedInvoiceId",lower(btrim("SourceEvidenceReference")));
            
            CREATE TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "AdjustmentId" uuid NOT NULL REFERENCES
                    "BuildingManagementMonthlyInvoiceAdjustments" ("Id") ON DELETE RESTRICT,
                "IssuedInvoiceId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "ApprovedSignedDeltaAmount" numeric(18,0) NOT NULL,
                "ReviewedByUserId" uuid NOT NULL,
                "ReviewedAtUtc" timestamptz NOT NULL,
                "Accepted" boolean NOT NULL,
                "ReviewReason" varchar(500) NOT NULL,
                CONSTRAINT "CK_BM_MonthlyAdjustmentDecision_Valid"
                  CHECK (length(btrim("ReviewReason")) BETWEEN 12 AND 500
                    AND ("Accepted" = TRUE OR "ApprovedSignedDeltaAmount" = 0))
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyAdjustment_Decision"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions" ("AdjustmentId");
            
            CREATE FUNCTION "BM_GuardMonthlyAdjustmentProposal"()
            RETURNS trigger LANGUAGE plpgsql AS $proposal$
            DECLARE issued record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly adjustment proposal is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO issued
                  FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                 WHERE "Id" = NEW."IssuedInvoiceId" FOR SHARE;
                IF issued."Id" IS NULL
                   OR issued."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR issued."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR issued."CandidateSha256" IS DISTINCT FROM NEW."InvoiceEvidenceSha256"
                   OR NEW."ProposedAtUtc" < issued."IssuedAtUtc" THEN
                   RAISE EXCEPTION 'Unanchored monthly adjustment proposal'
                       USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $proposal$;
            CREATE TRIGGER "TR_BM_MonthlyAdjustmentImmutable"
            BEFORE INSERT OR UPDATE OR DELETE
            ON "BuildingManagementMonthlyInvoiceAdjustments"
            FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyAdjustmentProposal"();
            
            CREATE FUNCTION "BM_GuardMonthlyAdjustmentDecision"()
            RETURNS trigger LANGUAGE plpgsql AS $decision$
            DECLARE proposal record;
            DECLARE issued record;
            DECLARE previous_delta numeric(18,0);
            DECLARE received numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly adjustment decision is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO proposal
                  FROM "BuildingManagementMonthlyInvoiceAdjustments"
                 WHERE "Id" = NEW."AdjustmentId" FOR SHARE;
                IF proposal."Id" IS NULL THEN
                   RAISE EXCEPTION 'Unknown monthly adjustment' USING ERRCODE = '23514';
                END IF;
                -- Same lock as monthly receipt allocation. PostgreSQL serializes
                -- credit approvals against a concurrently arriving bank receipt.
                SELECT * INTO issued
                  FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                 WHERE "Id" = proposal."IssuedInvoiceId" FOR UPDATE;
                IF issued."Id" IS NULL
                   OR NEW."IssuedInvoiceId" IS DISTINCT FROM issued."Id"
                   OR NEW."ContractId" IS DISTINCT FROM issued."ContractId"
                   OR proposal."InvoiceEvidenceSha256" IS DISTINCT FROM issued."CandidateSha256"
                   OR NEW."ReviewedByUserId" = proposal."ProposedByUserId"
                   OR NEW."ReviewedAtUtc" < proposal."ProposedAtUtc"
                   OR NEW."ApprovedSignedDeltaAmount" IS DISTINCT FROM
                      CASE WHEN NEW."Accepted" THEN proposal."SignedDeltaAmount"
                           ELSE 0::numeric END THEN
                   RAISE EXCEPTION 'Monthly independent adjustment approval invalid'
                       USING ERRCODE = '23514';
                END IF;
                IF NEW."Accepted" THEN
                   SELECT COALESCE(sum(d."ApprovedSignedDeltaAmount"),0)
                     INTO previous_delta
                     FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
                    WHERE d."IssuedInvoiceId" = issued."Id" AND d."Accepted" = TRUE;
                   SELECT COALESCE(sum(a."Amount"),0) INTO received
                     FROM "BuildingManagementMonthlyInvoicePaymentAllocation" a
                    WHERE a."IssuedInvoiceId" = issued."Id";
                   IF issued."TotalAmount" + previous_delta +
                          NEW."ApprovedSignedDeltaAmount" <= 0
                      OR issued."TotalAmount" + previous_delta +
                          NEW."ApprovedSignedDeltaAmount" < received THEN
                      RAISE EXCEPTION 'Credit note would erase captured bank receipts or invoice'
                          USING ERRCODE = '23514';
                   END IF;
                END IF;
                RETURN NEW;
            END;
            $decision$;
            CREATE TRIGGER "TR_BM_MonthlyAdjustmentDecisionImmutable"
            BEFORE INSERT OR UPDATE OR DELETE
            ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
            FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyAdjustmentDecision"();
            
            -- A debit correction may now expand, while credit correction reduces,
            -- the payable ceiling; still serialized on the issued invoice row.
            CREATE OR REPLACE FUNCTION "BM_GuardMonthlyPaymentAllocation"()
                        RETURNS trigger LANGUAGE plpgsql AS $allocation_guard$
                        DECLARE invoice record;
                        DECLARE payment record;
                        DECLARE already_allocated numeric(18,0);
                        DECLARE accepted_adjustments numeric(18,0);
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
                            SELECT COALESCE(sum(d."ApprovedSignedDeltaAmount"),0)
                              INTO accepted_adjustments
                              FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
                             WHERE d."IssuedInvoiceId" = invoice."Id" AND d."Accepted" = TRUE;
                            IF already_allocated + NEW."Amount" >
                                invoice."TotalAmount" + accepted_adjustments THEN
                                RAISE EXCEPTION 'Monthly allocation would exceed independently approved adjusted invoice total'
                                    USING ERRCODE = '23514';
                            END IF;
                            RETURN NEW;
                        END;
                        $allocation_guard$;
            
            CREATE FUNCTION "BM_RejectMonthlyAdjustmentTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $adj_truncate$
            BEGIN
                RAISE EXCEPTION 'Audited monthly adjustments cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $adj_truncate$;
            CREATE TRIGGER "TR_BM_MonthlyAdjustmentNoTruncate"
            BEFORE TRUNCATE ON "BuildingManagementMonthlyInvoiceAdjustments"
            FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectMonthlyAdjustmentTruncate"();
            CREATE TRIGGER "TR_BM_MonthlyDecisionNoTruncate"
            BEFORE TRUNCATE ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
            FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectMonthlyAdjustmentTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions",
                       "BuildingManagementMonthlyInvoiceAdjustments",
                       "BuildingManagementMonthlyInvoicePaymentAllocation"
            IN ACCESS EXCLUSIVE MODE;
            DO $adjustment_down$
            BEGIN
              IF EXISTS (SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustments" LIMIT 1)
                OR EXISTS (SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" LIMIT 1)
              THEN
                 RAISE EXCEPTION 'Audited invoice correction history exists; rollback forbidden'
                   USING ERRCODE = '23514';
              END IF;
            END;
            $adjustment_down$;
            CREATE OR REPLACE FUNCTION "BM_GuardMonthlyPaymentAllocation"()
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
            DROP TRIGGER "TR_BM_MonthlyDecisionNoTruncate"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            DROP TRIGGER "TR_BM_MonthlyAdjustmentNoTruncate"
              ON "BuildingManagementMonthlyInvoiceAdjustments";
            DROP FUNCTION "BM_RejectMonthlyAdjustmentTruncate"();
            DROP TRIGGER "TR_BM_MonthlyAdjustmentDecisionImmutable"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            DROP FUNCTION "BM_GuardMonthlyAdjustmentDecision"();
            DROP TRIGGER "TR_BM_MonthlyAdjustmentImmutable"
              ON "BuildingManagementMonthlyInvoiceAdjustments";
            DROP FUNCTION "BM_GuardMonthlyAdjustmentProposal"();
            DROP TABLE "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            DROP TABLE "BuildingManagementMonthlyInvoiceAdjustments";
            """);
    }
}
