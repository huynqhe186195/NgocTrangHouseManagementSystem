using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Atomic OI-04 ledger event: the SAME immutable row debits held deposit and
/// credits independently verified official debt by an identical VND amount.
/// The prior signed Owner allocation is the only permissible source.
/// No refund, bank confirmation or normal move-out closeout is performed.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101023000_Phase2G4AtomicDepositDebtOffsetPosting")]
public sealed class Phase2G4AtomicDepositDebtOffsetPosting : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE TABLE "BuildingManagementContractDepositDebtOffsetPostings" (
              "Id" uuid NOT NULL PRIMARY KEY,
              "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
              "MoveOutRequestId" uuid NOT NULL REFERENCES "BuildingManagementMoveOutRequests"("Id") ON DELETE RESTRICT,
              "BuildingId" uuid NOT NULL,
              "ApprovalId" uuid NOT NULL REFERENCES "BuildingManagementContractDepositDebtOffsetApprovals"("Id") ON DELETE RESTRICT,
              "PostedByOwnerId" uuid NOT NULL,
              "PostedAtUtc" timestamptz NOT NULL,
              "VerifiedHeldAmount" numeric(18,0) NOT NULL,
              "DebitHeldDepositVnd" numeric(18,0) NOT NULL,
              "CreditOfficialDebtVnd" numeric(18,0) NOT NULL,
              "RemainingHeldDepositVnd" numeric(18,0) NOT NULL,
              "RemainingOfficialDebtVnd" numeric(18,0) NOT NULL,
              "ApprovalSha256" varchar(64) NOT NULL,
              "PostingJson" text NOT NULL,
              "PostingSha256" varchar(64) NOT NULL,
              CONSTRAINT "CK_BM_OI04OffsetPostedBalanced" CHECK (
                "VerifiedHeldAmount" > 0
                AND "VerifiedHeldAmount" = "DebitHeldDepositVnd" + "RemainingHeldDepositVnd"
                AND "DebitHeldDepositVnd" > 0
                AND "DebitHeldDepositVnd" = "CreditOfficialDebtVnd"
                AND "RemainingHeldDepositVnd" >= 0
                AND "RemainingOfficialDebtVnd" >= 0
                AND "ApprovalSha256" ~ '^[0-9a-f]{64}$'
                AND "PostingSha256" ~ '^[0-9a-f]{64}$'
                AND length("PostingJson") BETWEEN 100 AND 1000000
                AND jsonb_typeof("PostingJson"::jsonb) = 'object'
              )
            );
            CREATE UNIQUE INDEX "UX_BM_OffsetPosted_Contract"
              ON "BuildingManagementContractDepositDebtOffsetPostings"("ContractId");
            CREATE UNIQUE INDEX "UX_BM_OffsetPosted_Request"
              ON "BuildingManagementContractDepositDebtOffsetPostings"("MoveOutRequestId");
            CREATE UNIQUE INDEX "UX_BM_OffsetPosted_Approval"
              ON "BuildingManagementContractDepositDebtOffsetPostings"("ApprovalId");

            CREATE FUNCTION "BM_GuardOI04OffsetPosting"()
            RETURNS trigger LANGUAGE plpgsql AS $post$
            DECLARE a record;
            DECLARE c record;
            DECLARE req record;
            DECLARE approval_json jsonb;
            DECLARE execution_json jsonb;
            DECLARE actual_count bigint;
            DECLARE claimed_count bigint;
            DECLARE debt_total numeric;
            DECLARE frozen_total numeric;
            BEGIN
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'Internal deposit offset posting is immutable'
                  USING ERRCODE = '23514';
              END IF;
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'Internal debt offset requires SERIALIZABLE commit'
                  USING ERRCODE = '23514';
              END IF;
              SELECT * INTO c FROM "BuildingManagementContracts"
                WHERE "Id"=NEW."ContractId" FOR UPDATE;
              SELECT * INTO req FROM "BuildingManagementMoveOutRequests"
                WHERE "Id"=NEW."MoveOutRequestId" FOR SHARE;
              SELECT * INTO a FROM "BuildingManagementContractDepositDebtOffsetApprovals"
                WHERE "Id"=NEW."ApprovalId" FOR SHARE;
              IF c."Id" IS NULL OR req."Id" IS NULL OR a."Id" IS NULL OR
                 c."Status" IS DISTINCT FROM 2 OR
                 req."Status" IS DISTINCT FROM 1 OR
                 req."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                 req."RoomId" IS DISTINCT FROM c."RoomId" OR
                 a."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                 a."MoveOutRequestId" IS DISTINCT FROM req."Id" OR
                 a."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                 a."ApprovedByOwnerId" IS DISTINCT FROM NEW."PostedByOwnerId" OR
                 a."ApprovalSha256" IS DISTINCT FROM NEW."ApprovalSha256" OR
                 a."ApprovedOffsetAmount" IS DISTINCT FROM NEW."DebitHeldDepositVnd" OR
                 a."ExpectedRemainingDeposit" IS DISTINCT FROM NEW."RemainingHeldDepositVnd" OR
                 a."ExpectedUncoveredDebt" IS DISTINCT FROM NEW."RemainingOfficialDebtVnd" OR
                 a."VerifiedHeldAmount" IS DISTINCT FROM NEW."VerifiedHeldAmount" OR
                 c."DepositAmount" IS DISTINCT FROM a."VerifiedHeldAmount" OR
                 NEW."PostedAtUtc" < a."ApprovedAtUtc" OR
                 NEW."PostedAtUtc" > clock_timestamp() OR
                 EXISTS (SELECT 1 FROM "BuildingManagementContractDepositSettlementDecisions"
                          WHERE "ContractId"=NEW."ContractId") OR
                 EXISTS (SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                          WHERE "ContractId"=NEW."ContractId") OR
                 EXISTS (SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger"
                          WHERE "ContractId"=NEW."ContractId") OR
                 EXISTS (SELECT 1 FROM "BuildingManagementDepositSettlements"
                          WHERE "SourceContractId"=NEW."ContractId"
                            AND "Type" IN (1,2,4,5,6))
              THEN
                RAISE EXCEPTION 'Offset execution is stale, unscoped or has an earlier disposition'
                  USING ERRCODE = '23514';
              END IF;

              approval_json := a."ApprovalJson"::jsonb;
              execution_json := NEW."PostingJson"::jsonb;
              IF (execution_json->>'ContractId')::uuid IS DISTINCT FROM NEW."ContractId" OR
                 (execution_json->>'MoveOutRequestId')::uuid IS DISTINCT FROM NEW."MoveOutRequestId" OR
                 (execution_json->>'BuildingId')::uuid IS DISTINCT FROM NEW."BuildingId" OR
                 (execution_json->>'ApprovalId')::uuid IS DISTINCT FROM NEW."ApprovalId" OR
                 (execution_json->>'PostedByOwnerId')::uuid IS DISTINCT FROM NEW."PostedByOwnerId" OR
                 (execution_json->>'PostedAtUtc')::timestamptz IS DISTINCT FROM NEW."PostedAtUtc" OR
                 (execution_json->>'VerifiedHeldAmount')::numeric IS DISTINCT FROM NEW."VerifiedHeldAmount" OR
                 (execution_json->>'DebitHeldDepositVnd')::numeric IS DISTINCT FROM NEW."DebitHeldDepositVnd" OR
                 (execution_json->>'CreditOfficialDebtVnd')::numeric IS DISTINCT FROM NEW."CreditOfficialDebtVnd" OR
                 (execution_json->>'RemainingHeldDepositVnd')::numeric IS DISTINCT FROM NEW."RemainingHeldDepositVnd" OR
                 (execution_json->>'RemainingOfficialDebtVnd')::numeric IS DISTINCT FROM NEW."RemainingOfficialDebtVnd" OR
                 execution_json->>'ApprovalSha256' IS DISTINCT FROM NEW."ApprovalSha256" OR
                 execution_json->'ApprovedLines' IS DISTINCT FROM approval_json->'ApprovedLines' OR
                 encode(sha256(convert_to(NEW."PostingJson",'UTF8')),'hex') IS DISTINCT FROM
                   NEW."PostingSha256" OR
                 encode(sha256(convert_to(a."ApprovalJson",'UTF8')),'hex') IS DISTINCT FROM
                   a."ApprovalSha256"
              THEN
                RAISE EXCEPTION 'Internal posting does not reproduce exact signed Owner lines'
                  USING ERRCODE = '23514';
              END IF;
              SELECT count(*),COALESCE(sum((l->>'ProposedAppliedVnd')::numeric),0)
                INTO claimed_count, frozen_total
                FROM jsonb_array_elements(execution_json->'ApprovedLines') l;
              IF claimed_count = 0 OR claimed_count > 250 OR
                 frozen_total IS DISTINCT FROM NEW."CreditOfficialDebtVnd" OR
                 EXISTS (
                   SELECT 1 FROM jsonb_array_elements(execution_json->'ApprovedLines') l
                    WHERE (l->>'ProposedAppliedVnd')::numeric < 0 OR
                          (l->>'RemainingDebtVnd')::numeric < 0 OR
                          (l->>'OriginalOutstandingVnd')::numeric IS DISTINCT FROM
                            (l->>'ProposedAppliedVnd')::numeric +
                            (l->>'RemainingDebtVnd')::numeric
                 )
              THEN
                RAISE EXCEPTION 'Posting cannot have unbalanced or invented debt lines'
                  USING ERRCODE = '23514';
              END IF;

              -- All monthly invoices MUST be posted and unadjusted, final
              -- issuance/corrections are unsupported until separate policy.
              IF EXISTS (
                  SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  WHERE i."ContractId"=NEW."ContractId"
                    AND NOT EXISTS (
                      SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                       WHERE p."Kind"=1 AND p."IssuedInvoiceId"=i."Id"
                         AND p."ContractId"=NEW."ContractId")
              ) OR EXISTS (
                  SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustments" x
                  JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                    ON i."Id"=x."IssuedInvoiceId"
                  WHERE i."ContractId"=NEW."ContractId"
              ) OR EXISTS (
                  SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" x
                  JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                    ON i."Id"=x."IssuedInvoiceId"
                  WHERE i."ContractId"=NEW."ContractId"
              ) OR EXISTS (
                  SELECT 1 FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
                  JOIN "BuildingManagementOfficialInvoiceReceivables" p
                    ON p."Id"=r."ReceivablePostingId"
                  WHERE p."ContractId"=NEW."ContractId"
                    AND r."ReviewedAtUtc" > a."ApprovedAtUtc"
              ) THEN
                RAISE EXCEPTION 'Changes to official receivable evidence after approval require a new decision'
                  USING ERRCODE = '23514';
              END IF;

              -- Source snapshot is recalculated at posting time, not merely
              -- read from Owner JSON. Exactly match one current charge per
              -- frozen claim (source SHA+category). No external bank proof is
              -- invented: already-reviewed receipts remain distinct.
              WITH expected AS (
                SELECT p."SourceEvidenceSha256" AS reference,
                       v.category, v.original_amount -
                       COALESCE((SELECT sum(l."Amount")
                         FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                        WHERE l."ReceivablePostingId"=p."Id"
                          AND l."Category"=v.category),0) AS unpaid
                FROM "BuildingManagementOfficialInvoiceReceivables" p
                JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  ON i."Id"=p."IssuedInvoiceId"
                JOIN "BuildingManagementMonthlyInvoiceCandidates" c
                  ON c."Id"=i."CandidateId"
                CROSS JOIN LATERAL (VALUES
                   (1,c."RentAmount"),(2,c."ElectricityAmount"),
                   (3,c."WaterAmount"),(4,c."AuxiliaryAmount")
                ) v(category,original_amount)
                WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1
                  AND c."FrozenEvidenceSha256"=p."SourceEvidenceSha256"
              ), claims AS (
                SELECT x."Category",x."OutstandingVnd",
                  x."SignedEvidenceReference",x."IndependentlyVerified",x."IsDisputed"
                  FROM jsonb_to_recordset(approval_json->'Claims') x(
                    "Category" integer,"OutstandingVnd" numeric,
                    "SignedEvidenceReference" text,"IndependentlyVerified" boolean,
                    "IsDisputed" boolean)
              ), match AS (
                SELECT c."Category",c."OutstandingVnd",
                       e.unpaid,e.reference,
                       c."IndependentlyVerified",c."IsDisputed"
                  FROM claims c LEFT JOIN expected e
                    ON c."SignedEvidenceReference"=e.reference
                   AND c."Category"=e.category
              )
              SELECT count(*),COALESCE(sum("OutstandingVnd"),0)
                INTO actual_count,debt_total FROM match
               WHERE reference IS NOT NULL AND
                     unpaid="OutstandingVnd" AND unpaid>0 AND
                     "IndependentlyVerified"=TRUE AND "IsDisputed"=FALSE;
              IF actual_count IS DISTINCT FROM claimed_count OR
                 EXISTS (
                   SELECT 1 FROM (
                     SELECT p."Id",v.category
                     FROM "BuildingManagementOfficialInvoiceReceivables" p
                     JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                       ON i."Id"=p."IssuedInvoiceId"
                     JOIN "BuildingManagementMonthlyInvoiceCandidates" c
                       ON c."Id"=i."CandidateId"
                     CROSS JOIN LATERAL (VALUES
                         (1,c."RentAmount"),(2,c."ElectricityAmount"),
                         (3,c."WaterAmount"),(4,c."AuxiliaryAmount")
                     ) v(category,original_amount)
                     WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1 AND
                       v.original_amount-COALESCE((SELECT sum(l."Amount")
                         FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                         WHERE l."ReceivablePostingId"=p."Id"
                         AND l."Category"=v.category),0)>0
                   ) all_due
                   HAVING count(*)<>actual_count
                 ) OR
                 debt_total IS DISTINCT FROM
                   NEW."CreditOfficialDebtVnd" + NEW."RemainingOfficialDebtVnd"
              THEN
                RAISE EXCEPTION 'Current official debt category differs from Owner-approved source'
                  USING ERRCODE = '23514';
              END IF;
              RETURN NEW;
            END;
            $post$;

            -- Reuse the prior cash-backed guard at actual posting too,
            -- so no receipt can disappear after signed review/approval.
            CREATE TRIGGER "TR_BM_00_CashBackedDebtPosting"
              BEFORE INSERT ON "BuildingManagementContractDepositDebtOffsetPostings"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOI04CashBackedApproval"();
            CREATE TRIGGER "TR_BM_10_VerifyDebtOffsetPosting"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositDebtOffsetPostings"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOI04OffsetPosting"();

            CREATE FUNCTION "BM_DenyOffsetHistoryTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $notrunc$
            BEGIN
              RAISE EXCEPTION 'Offset journal cannot be truncated'
                USING ERRCODE='23514';
            END;
            $notrunc$;
            CREATE TRIGGER "TR_BM_OI04OffsetPostingNoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementContractDepositDebtOffsetPostings"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_DenyOffsetHistoryTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtOffsetPostings"
                IN ACCESS EXCLUSIVE MODE;
            DO $history$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                 "BuildingManagementContractDepositDebtOffsetPostings" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot erase posted cash/debt offset history'
                  USING ERRCODE='23514';
              END IF;
            END;
            $history$;
            DROP TRIGGER "TR_BM_OI04OffsetPostingNoTruncate"
              ON "BuildingManagementContractDepositDebtOffsetPostings";
            DROP TRIGGER "TR_BM_10_VerifyDebtOffsetPosting"
              ON "BuildingManagementContractDepositDebtOffsetPostings";
            DROP TRIGGER "TR_BM_00_CashBackedDebtPosting"
              ON "BuildingManagementContractDepositDebtOffsetPostings";
            DROP FUNCTION "BM_DenyOffsetHistoryTruncate"();
            DROP FUNCTION "BM_GuardOI04OffsetPosting"();
            DROP TABLE "BuildingManagementContractDepositDebtOffsetPostings";
            """);
    }
}
