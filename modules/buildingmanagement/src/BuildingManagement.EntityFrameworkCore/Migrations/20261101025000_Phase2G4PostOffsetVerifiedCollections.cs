using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Narrowly resumes genuine new debt receipts and ONE legitimate FINAL
/// invoice after an OI-04 offset, without ever reopening past evidence.
/// All older 2G.4 freezes remain for source/candidate/deposit changes.
/// No refund is executed by these receipts.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101025000_Phase2G4PostOffsetVerifiedCollections")]
public sealed class Phase2G4PostOffsetVerifiedCollections : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE FUNCTION "BM_PostOffsetAppliedToInvoice"(contract uuid, posting uuid)
            RETURNS numeric LANGUAGE sql STABLE AS $a$
              SELECT COALESCE(sum((line->>'ProposedAppliedVnd')::numeric),0)
              FROM "BuildingManagementContractDepositDebtOffsetPostings" o
              JOIN "BuildingManagementContractDepositDebtOffsetApprovals" a
                ON a."Id"=o."ApprovalId"
              JOIN "BuildingManagementOfficialInvoiceReceivables" p
                ON p."Id"=posting AND p."ContractId"=contract AND p."Kind"=1
              CROSS JOIN LATERAL jsonb_array_elements(o."PostingJson"::jsonb->'ApprovedLines') line
              JOIN LATERAL jsonb_array_elements(a."ApprovalJson"::jsonb->'Claims') claim
                ON claim->>'ClaimId'=line->>'ClaimId'
               AND claim->>'SignedEvidenceReference'=p."SourceEvidenceSha256"
              WHERE o."ContractId"=contract
            $a$;

            CREATE FUNCTION "BM_PostOffsetAppliedToMonthlyCategory"(
                contract uuid, posting uuid, category integer)
            RETURNS numeric LANGUAGE sql STABLE AS $a$
              SELECT COALESCE(sum((line->>'ProposedAppliedVnd')::numeric),0)
              FROM "BuildingManagementContractDepositDebtOffsetPostings" o
              JOIN "BuildingManagementContractDepositDebtOffsetApprovals" a
                ON a."Id"=o."ApprovalId"
              JOIN "BuildingManagementOfficialInvoiceReceivables" p
                ON p."Id"=posting AND p."ContractId"=contract AND p."Kind"=1
              CROSS JOIN LATERAL jsonb_array_elements(o."PostingJson"::jsonb->'ApprovedLines') line
              JOIN LATERAL jsonb_array_elements(a."ApprovalJson"::jsonb->'Claims') claim
                ON claim->>'ClaimId'=line->>'ClaimId'
               AND claim->>'SignedEvidenceReference'=p."SourceEvidenceSha256"
               AND (line->>'Category')::integer=category
              WHERE o."ContractId"=contract
            $a$;

            CREATE FUNCTION "BM_GuardPostOffsetNewEvidence"()
            RETURNS trigger LANGUAGE plpgsql AS $newsource$
            DECLARE contract_id uuid;
            DECLARE offset_row record;
            DECLARE source_row record;
            DECLARE prior_amount numeric;
            DECLARE credit numeric;
            DECLARE current_receivable record;
            DECLARE source_invoice record;
            DECLARE category_ceiling numeric;
            BEGIN
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'Post-offset source history cannot be changed'
                  USING ERRCODE='23514';
              END IF;

              -- NEW is an anonymous trigger RECORD. Access columns
              -- through JSON so a Payment INSERT never attempts to resolve
              -- the line-only ReceivablePostingId field (SQLSTATE 42703).
              contract_id := CASE TG_TABLE_NAME
                WHEN 'BuildingManagementOfficialMonthlyReceiptLineAllocations'
                  THEN (SELECT p."ContractId"
                        FROM "BuildingManagementOfficialInvoiceReceivables" p
                       WHERE p."Id"=NULLIF(to_jsonb(NEW)->>'ReceivablePostingId','')::uuid)
                ELSE NULLIF(to_jsonb(NEW)->>'ContractId','')::uuid
                END;
              SELECT * INTO offset_row
                FROM "BuildingManagementContractDepositDebtOffsetPostings"
               WHERE "ContractId"=contract_id;
              IF offset_row."Id" IS NULL THEN RETURN NEW; END IF;
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'Post-offset financial entries require SERIALIZABLE'
                  USING ERRCODE='23514';
              END IF;
              PERFORM 1 FROM "BuildingManagementContracts"
                WHERE "Id"=contract_id FOR UPDATE;
              IF TG_TABLE_NAME = 'BuildingManagementPayments' THEN
                -- Existing Payment and existing monthly/final allocation
                -- journal are the ONLY receivable-cash source; there is no
                -- parallel private ledger or fabricated account balance.
                SELECT * INTO current_receivable
                  FROM "BuildingManagementOfficialInvoiceReceivables"
                 WHERE "ContractId"=contract_id AND "IssuedInvoiceId"=NEW."InvoiceId"
                 FOR UPDATE;
                IF current_receivable."Id" IS NULL OR NEW."Purpose"<>3 OR
                   NEW."Method"<>2 OR NEW."Channel"<>1 OR
                   NEW."Status"<>2 OR NEW."IsDeleted" IS DISTINCT FROM FALSE OR
                   NEW."RoomReservationId" IS NOT NULL OR
                   NEW."DepositDisposition" IS NOT NULL OR
                   NEW."PaidAt" IS NULL OR
                   NEW."PaidAt"<offset_row."PostedAtUtc" OR
                   NEW."TransactionReference" IS NULL OR
                   length(btrim(NEW."TransactionReference")) NOT BETWEEN 8 AND 128 OR
                   NEW."Notes" NOT LIKE 'BANK-PROOF: %' OR
                   NEW."Amount"<=0 OR NEW."Amount"<>trunc(NEW."Amount")
                THEN
                  RAISE EXCEPTION 'Only new documented invoice bank receipts are allowed after offset'
                    USING ERRCODE='23514';
                END IF;
                SELECT COALESCE(sum(pay."Amount"),0) INTO prior_amount
                  FROM "BuildingManagementPayments" pay
                 WHERE pay."ContractId"=contract_id
                   AND pay."InvoiceId"=NEW."InvoiceId"
                   AND pay."Purpose"=3 AND pay."IsDeleted"=FALSE
                   AND pay."Status"=2;
                credit := "BM_PostOffsetAppliedToInvoice"(
                    contract_id,current_receivable."Id");
                IF prior_amount+NEW."Amount"+credit>current_receivable."OriginalAmount" THEN
                  RAISE EXCEPTION 'New cash receipt exceeds debt remaining after internal offset'
                    USING ERRCODE='23514';
                END IF;
                RETURN NEW;
              ELSIF TG_TABLE_NAME = 'BuildingManagementOfficialInvoiceReceiptReviews' THEN
                SELECT * INTO current_receivable
                  FROM "BuildingManagementOfficialInvoiceReceivables"
                 WHERE "Id"=NEW."ReceivablePostingId"
                   AND "ContractId"=contract_id FOR UPDATE;
                IF current_receivable."Id" IS NULL THEN
                  RAISE EXCEPTION 'Post-offset receipt review has no official debt'
                    USING ERRCODE='23514';
                END IF;
                credit:="BM_PostOffsetAppliedToInvoice"(
                    contract_id,current_receivable."Id");
                IF NEW."Accepted" THEN
                  SELECT COALESCE(sum(rr."Amount"),0) INTO prior_amount
                    FROM "BuildingManagementOfficialInvoiceReceiptReviews" rr
                   WHERE rr."ReceivablePostingId"=current_receivable."Id"
                     AND rr."Accepted"=TRUE;
                  IF prior_amount+NEW."Amount"+credit>current_receivable."OriginalAmount" THEN
                    RAISE EXCEPTION 'Reviewed bank receipts cannot overpay net-of-offset debt'
                      USING ERRCODE='23514';
                  END IF;
                END IF;
                RETURN NEW;
              ELSIF TG_TABLE_NAME = 'BuildingManagementOfficialMonthlyReceiptLineAllocations' THEN
                SELECT * INTO current_receivable
                  FROM "BuildingManagementOfficialInvoiceReceivables"
                 WHERE "Id"=NEW."ReceivablePostingId"
                   AND "ContractId"=contract_id AND "Kind"=1 FOR UPDATE;
                SELECT c.* INTO source_invoice
                  FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  JOIN "BuildingManagementMonthlyInvoiceCandidates" c
                    ON c."Id"=i."CandidateId"
                 WHERE i."Id"=current_receivable."IssuedInvoiceId";
                IF source_invoice."Id" IS NULL THEN
                  RAISE EXCEPTION 'No original category source for post-offset receipt'
                    USING ERRCODE='23514';
                END IF;
                category_ceiling := CASE NEW."Category"
                  WHEN 1 THEN source_invoice."RentAmount"
                  WHEN 2 THEN source_invoice."ElectricityAmount"
                  WHEN 3 THEN source_invoice."WaterAmount"
                  WHEN 4 THEN source_invoice."AuxiliaryAmount"
                  ELSE NULL END;
                SELECT COALESCE(sum(x."Amount"),0) INTO prior_amount
                  FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" x
                 WHERE x."ReceivablePostingId"=NEW."ReceivablePostingId"
                   AND x."Category"=NEW."Category";
                credit := "BM_PostOffsetAppliedToMonthlyCategory"(
                    contract_id,current_receivable."Id",NEW."Category");
                IF category_ceiling IS NULL OR
                   prior_amount + NEW."Amount" + credit > category_ceiling THEN
                  RAISE EXCEPTION 'Post-offset bank allocation would double-credit an invoice line'
                    USING ERRCODE='23514';
                END IF;
                RETURN NEW;
              ELSIF TG_TABLE_NAME = 'BuildingManagementIssuedInvoiceLedger' THEN
                -- A new independently reviewed FINAL bill for this same
                -- approved move-out is REQUIRED after the earlier monthly
                -- debt offset, but may be issued only once.
                IF NEW."MoveOutRequestId" IS DISTINCT FROM offset_row."MoveOutRequestId" OR
                   EXISTS (SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                       WHERE i."ContractId"=contract_id) THEN
                   RAISE EXCEPTION 'Only first final invoice for original move-out can follow offset'
                     USING ERRCODE='23514';
                END IF;
                RETURN NEW;
              ELSIF TG_TABLE_NAME = 'BuildingManagementOfficialInvoiceReceivables' THEN
                IF NEW."Kind"<>2 OR
                   EXISTS (SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                     WHERE p."ContractId"=contract_id AND p."Kind"=2) OR
                   NOT EXISTS (
                     SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                      WHERE i."Id"=NEW."IssuedInvoiceId"
                        AND i."ContractId"=contract_id
                        AND i."MoveOutRequestId"=offset_row."MoveOutRequestId") THEN
                  RAISE EXCEPTION 'Only one original final receivable can follow offset'
                    USING ERRCODE='23514';
                END IF;
                RETURN NEW;
              END IF;
              RAISE EXCEPTION 'Unsupported post-offset source mutation'
                USING ERRCODE='23514';
            END;
            $newsource$;

            DROP TRIGGER "TR_BM_OI04FreezeOffsetPayments"
              ON "BuildingManagementPayments";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetReceiptReviews"
              ON "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetMonthlyLineCredits"
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetFinalIssued"
              ON "BuildingManagementIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetOfficialPostings"
              ON "BuildingManagementOfficialInvoiceReceivables";

            CREATE TRIGGER "TR_BM_OI04NetOffsetCashSource"
              BEFORE INSERT OR UPDATE OR DELETE ON "BuildingManagementPayments"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetNewEvidence"();
            CREATE TRIGGER "TR_BM_OI04NetOffsetBankReview"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceiptReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetNewEvidence"();
            CREATE TRIGGER "TR_BM_OI04NetOffsetMonthlyAllocation"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetNewEvidence"();
            CREATE TRIGGER "TR_BM_OI04NetOffsetFinalIssued"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetNewEvidence"();
            CREATE TRIGGER "TR_BM_OI04NetOffsetFinalReceivable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceivables"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetNewEvidence"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtOffsetPostings"
              IN ACCESS EXCLUSIVE MODE;
            DO $downgrade$
            BEGIN
              IF EXISTS (SELECT 1
                FROM "BuildingManagementContractDepositDebtOffsetPostings" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot remove post-offset collection rules with signed posting history'
                  USING ERRCODE='23514';
              END IF;
            END;
            $downgrade$;
            DROP TRIGGER "TR_BM_OI04NetOffsetCashSource"
              ON "BuildingManagementPayments";
            DROP TRIGGER "TR_BM_OI04NetOffsetBankReview"
              ON "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP TRIGGER "TR_BM_OI04NetOffsetMonthlyAllocation"
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            DROP TRIGGER "TR_BM_OI04NetOffsetFinalIssued"
              ON "BuildingManagementIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_OI04NetOffsetFinalReceivable"
              ON "BuildingManagementOfficialInvoiceReceivables";

            CREATE TRIGGER "TR_BM_OI04FreezeOffsetPayments"
              BEFORE INSERT OR UPDATE OR DELETE ON "BuildingManagementPayments"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetReceiptReviews"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceiptReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetMonthlyLineCredits"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetFinalIssued"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetOfficialPostings"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceivables"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            DROP FUNCTION "BM_GuardPostOffsetNewEvidence"();
            DROP FUNCTION "BM_PostOffsetAppliedToMonthlyCategory"(uuid,uuid,integer);
            DROP FUNCTION "BM_PostOffsetAppliedToInvoice"(uuid,uuid);
            """);
    }
}
