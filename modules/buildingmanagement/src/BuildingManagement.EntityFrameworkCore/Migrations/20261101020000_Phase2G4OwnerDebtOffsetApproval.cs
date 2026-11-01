using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only approval journal. Prevents direct SQL from laundering
/// fabricated receivables or withdrawing deposit money; any eventual
/// settlement writer must separately revalidate at SERIALIZABLE COMMIT.
/// No existing 2G.5 no-offset checks are modified by this migration.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101020000_Phase2G4OwnerDebtOffsetApproval")]
public sealed class Phase2G4OwnerDebtOffsetApproval : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementContractDepositDebtOffsetApprovals" (
              "Id" uuid NOT NULL PRIMARY KEY,
              "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
              "MoveOutRequestId" uuid NOT NULL REFERENCES "BuildingManagementMoveOutRequests"("Id") ON DELETE RESTRICT,
              "BuildingId" uuid NOT NULL,
              "ApprovedByOwnerId" uuid NOT NULL,
              "IndependentDebtReviewerId" uuid NOT NULL,
              "ApprovedAtUtc" timestamptz NOT NULL,
              "VerifiedHeldAmount" numeric(18,0) NOT NULL,
              "ApprovedOffsetAmount" numeric(18,0) NOT NULL,
              "ExpectedRemainingDeposit" numeric(18,0) NOT NULL,
              "ExpectedUncoveredDebt" numeric(18,0) NOT NULL,
              "SignedPolicyReference" varchar(500) NOT NULL,
              "IndependentReviewReference" varchar(500) NOT NULL,
              "ApprovalJson" text NOT NULL,
              "ApprovalSha256" varchar(64) NOT NULL,
              CONSTRAINT "CK_BM_DebtOffsetApproval_Balanced" CHECK (
                "VerifiedHeldAmount" > 0
                AND "ApprovedOffsetAmount" > 0
                AND "ApprovedOffsetAmount" <= "VerifiedHeldAmount"
                AND "ExpectedRemainingDeposit" =
                    "VerifiedHeldAmount" - "ApprovedOffsetAmount"
                AND "ExpectedUncoveredDebt" >= 0
                AND "ApprovedByOwnerId" <> "IndependentDebtReviewerId"
                AND "ApprovedByOwnerId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "IndependentDebtReviewerId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND length(btrim("SignedPolicyReference")) BETWEEN 12 AND 500
                AND length(btrim("IndependentReviewReference")) BETWEEN 12 AND 500
                AND "ApprovalSha256" ~ '^[0-9a-f]{64}$'
                AND length("ApprovalJson") BETWEEN 100 AND 1000000
                AND jsonb_typeof("ApprovalJson"::jsonb) = 'object'
              )
            );
            CREATE UNIQUE INDEX "UX_BM_OffsetApproval_Contract"
              ON "BuildingManagementContractDepositDebtOffsetApprovals"("ContractId");
            CREATE UNIQUE INDEX "UX_BM_OffsetApproval_Request"
              ON "BuildingManagementContractDepositDebtOffsetApprovals"("MoveOutRequestId");

            CREATE FUNCTION "BM_GuardOwnerDebtOffsetApproval"()
            RETURNS trigger LANGUAGE plpgsql AS $guard$
            DECLARE c record;
            DECLARE req record;
            DECLARE j jsonb;
            DECLARE expected_count integer;
            DECLARE claim_count integer;
            DECLARE debt_sum numeric;
            DECLARE approved_sum numeric;
            BEGIN
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'Signed Owner debt-offset approval is immutable'
                  USING ERRCODE='23514';
              END IF;
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'Owner debt-offset approval requires SERIALIZABLE'
                  USING ERRCODE='23514';
              END IF;
              SELECT "Id","DepositAmount","Status","RoomId" INTO c
                FROM "BuildingManagementContracts"
               WHERE "Id"=NEW."ContractId" FOR UPDATE;
              SELECT "Id","ContractId","RoomId","Status" INTO req
                FROM "BuildingManagementMoveOutRequests"
               WHERE "Id"=NEW."MoveOutRequestId" FOR SHARE;
              IF c."Id" IS NULL OR req."Id" IS NULL OR
                 c."Status" IS DISTINCT FROM 2 OR
                 req."Status" IS DISTINCT FROM 1 OR
                 req."ContractId" IS DISTINCT FROM c."Id" OR
                 req."RoomId" IS DISTINCT FROM c."RoomId" OR
                 c."DepositAmount" IS DISTINCT FROM NEW."VerifiedHeldAmount" OR
                 NEW."ApprovedAtUtc" > clock_timestamp() OR
                 EXISTS (
                   SELECT 1 FROM "BuildingManagementContractDepositSettlementDecisions" d
                   WHERE d."ContractId"=NEW."ContractId"
                 ) OR EXISTS (
                   SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" a
                   WHERE a."ContractId"=NEW."ContractId"
                 ) OR EXISTS (
                   SELECT 1 FROM "BuildingManagementDepositSettlements" s
                   WHERE s."SourceContractId"=NEW."ContractId"
                     AND s."Type" IN (1,2,4,5,6)
                 )
              THEN
                RAISE EXCEPTION 'Offset approval conflicts with source contract or prior disposition'
                  USING ERRCODE='23514';
              END IF;

              j := NEW."ApprovalJson"::jsonb;
              IF (j->>'ContractId')::uuid IS DISTINCT FROM NEW."ContractId"
                 OR (j->>'MoveOutRequestId')::uuid IS DISTINCT FROM NEW."MoveOutRequestId"
                 OR (j->>'BuildingId')::uuid IS DISTINCT FROM NEW."BuildingId"
                 OR (j->>'ApprovedByOwnerId')::uuid IS DISTINCT FROM NEW."ApprovedByOwnerId"
                 OR (j->>'IndependentDebtReviewerId')::uuid IS DISTINCT FROM NEW."IndependentDebtReviewerId"
                 OR (j->>'VerifiedHeldAmount')::numeric IS DISTINCT FROM NEW."VerifiedHeldAmount"
                 OR (j->>'ApprovedOffsetAmount')::numeric IS DISTINCT FROM NEW."ApprovedOffsetAmount"
                 OR (j->>'ExpectedRemainingDeposit')::numeric IS DISTINCT FROM NEW."ExpectedRemainingDeposit"
                 OR (j->>'ExpectedUncoveredDebt')::numeric IS DISTINCT FROM NEW."ExpectedUncoveredDebt"
                 OR j->>'SignedPolicyReference' IS DISTINCT FROM NEW."SignedPolicyReference"
                 OR j->>'IndependentReviewReference' IS DISTINCT FROM NEW."IndependentReviewReference"
                 OR jsonb_typeof(j->'Claims') IS DISTINCT FROM 'array'
                 OR jsonb_typeof(j->'ApprovedLines') IS DISTINCT FROM 'array'
              THEN
                RAISE EXCEPTION 'Signed Owner proof differs from indexed approval'
                  USING ERRCODE='23514';
              END IF;

              -- No phantom categories: every claimed monthly charge must
              -- exactly match CURRENT archived charge minus all independently
              -- reviewed bank receipt-line allocations. Existing correction
              -- or final-invoice paths have no safe category split and HOLD.
              IF EXISTS (
                SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                 WHERE i."ContractId"=NEW."ContractId"
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustments" a
                JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  ON i."Id"=a."IssuedInvoiceId"
               WHERE i."ContractId"=NEW."ContractId"
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
                JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  ON i."Id"=d."IssuedInvoiceId"
               WHERE i."ContractId"=NEW."ContractId"
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                 WHERE i."ContractId"=NEW."ContractId" AND NOT EXISTS (
                   SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                    WHERE p."IssuedInvoiceId"=i."Id"
                      AND p."Kind"=1 AND p."ContractId"=i."ContractId")
              ) THEN
                RAISE EXCEPTION 'All unadjusted monthly invoices must be officially posted'
                  USING ERRCODE='23514';
              END IF;

              SELECT count(*) INTO expected_count
              FROM (
                SELECT p."Id",c."RentAmount",c."ElectricityAmount",
                       c."WaterAmount",c."AuxiliaryAmount"
                  FROM "BuildingManagementOfficialInvoiceReceivables" p
                  JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                    ON i."Id"=p."IssuedInvoiceId"
                  JOIN "BuildingManagementMonthlyInvoiceCandidates" c
                    ON c."Id"=i."CandidateId"
                 WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1
              ) historical;
              IF expected_count = 0 THEN
                RAISE EXCEPTION 'No verified official monthly receivable exists'
                  USING ERRCODE='23514';
              END IF;

              WITH expected AS (
                SELECT p."SourceEvidenceSha256" AS reference,
                       v.category,
                       v.original_amount -
                         COALESCE((
                           SELECT sum(l."Amount")
                             FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                            WHERE l."ReceivablePostingId"=p."Id"
                              AND l."Category"=v.category
                         ),0) AS amount
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
              ),
              claims AS (
                SELECT x."Category",x."OutstandingVnd",
                       x."SignedEvidenceReference",x."IndependentlyVerified",
                       x."IsDisputed"
                FROM jsonb_to_recordset(j->'Claims') AS x(
                  "Category" integer, "OutstandingVnd" numeric,
                  "SignedEvidenceReference" text,
                  "IndependentlyVerified" boolean, "IsDisputed" boolean)
              )
              SELECT count(*),COALESCE(sum("OutstandingVnd"),0)
                INTO claim_count,debt_sum
                FROM claims;
              -- Claim count and total also have to agree with *all*
              -- expected original fee-category debt; no selective omission.
              WITH expected AS (
                SELECT p."SourceEvidenceSha256" AS reference,
                       v.category,
                       v.original_amount -
                         COALESCE((SELECT sum(l."Amount")
                           FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                           WHERE l."ReceivablePostingId"=p."Id"
                             AND l."Category"=v.category),0) AS amount
                FROM "BuildingManagementOfficialInvoiceReceivables" p
                JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i ON
                    i."Id"=p."IssuedInvoiceId"
                JOIN "BuildingManagementMonthlyInvoiceCandidates" c ON
                    c."Id"=i."CandidateId"
                CROSS JOIN LATERAL (VALUES
                  (1,c."RentAmount"),(2,c."ElectricityAmount"),
                  (3,c."WaterAmount"),(4,c."AuxiliaryAmount")
                ) v(category,original_amount)
                WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1
                  AND c."FrozenEvidenceSha256"=p."SourceEvidenceSha256"
              ),
              claims AS (
                SELECT x."Category",x."OutstandingVnd",
                  x."SignedEvidenceReference",x."IndependentlyVerified",x."IsDisputed"
                FROM jsonb_to_recordset(j->'Claims') AS x(
                  "Category" integer,"OutstandingVnd" numeric,
                  "SignedEvidenceReference" text,
                  "IndependentlyVerified" boolean,"IsDisputed" boolean)
              )
              SELECT COALESCE(sum(e.amount),0) INTO approved_sum
                FROM expected e WHERE e.amount > 0;
              IF EXISTS (
                WITH expected AS (
                  SELECT p."SourceEvidenceSha256" AS reference,
                    v.category, v.original_amount -
                    COALESCE((SELECT sum(l."Amount")
                      FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                      WHERE l."ReceivablePostingId"=p."Id"
                        AND l."Category"=v.category),0) AS amount
                  FROM "BuildingManagementOfficialInvoiceReceivables" p
                  JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i ON
                       i."Id"=p."IssuedInvoiceId"
                  JOIN "BuildingManagementMonthlyInvoiceCandidates" c ON
                       c."Id"=i."CandidateId"
                  CROSS JOIN LATERAL (VALUES
                    (1,c."RentAmount"),(2,c."ElectricityAmount"),
                    (3,c."WaterAmount"),(4,c."AuxiliaryAmount")
                  ) v(category,original_amount)
                  WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1
                    AND c."FrozenEvidenceSha256"=p."SourceEvidenceSha256"
                ),
                claims AS (
                  SELECT x."Category",x."OutstandingVnd",
                    x."SignedEvidenceReference",x."IndependentlyVerified",
                    x."IsDisputed"
                  FROM jsonb_to_recordset(j->'Claims') AS x(
                    "Category" integer,"OutstandingVnd" numeric,
                    "SignedEvidenceReference" text,
                    "IndependentlyVerified" boolean,"IsDisputed" boolean)
                )
                SELECT 1 FROM (
                  SELECT reference,category,amount
                    FROM expected WHERE amount > 0
                ) e FULL OUTER JOIN claims c
                  ON c."SignedEvidenceReference"=e.reference
                 AND c."Category"=e.category
                WHERE e.reference IS NULL OR c."Category" IS NULL
                  OR c."OutstandingVnd" IS DISTINCT FROM e.amount
                  OR c."IndependentlyVerified" IS DISTINCT FROM TRUE
                  OR c."IsDisputed" IS DISTINCT FROM FALSE
              ) OR approved_sum IS DISTINCT FROM debt_sum OR
                claim_count IS DISTINCT FROM (
                  SELECT count(*) FROM (
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
                    WHERE p."ContractId"=NEW."ContractId" AND p."Kind"=1
                      AND c."FrozenEvidenceSha256"=p."SourceEvidenceSha256"
                      AND v.original_amount -
                      COALESCE((SELECT sum(l."Amount")
                        FROM "BuildingManagementOfficialMonthlyReceiptLineAllocations" l
                        WHERE l."ReceivablePostingId"=p."Id"
                          AND l."Category"=v.category),0) > 0
                  ) counted
                )
              THEN
                RAISE EXCEPTION 'Owner approval no longer matches all official original charge lines'
                  USING ERRCODE='23514';
              END IF;

              IF NEW."ApprovedOffsetAmount" IS DISTINCT FROM
                    LEAST(NEW."VerifiedHeldAmount", debt_sum)
                 OR NEW."ExpectedUncoveredDebt" IS DISTINCT FROM
                    GREATEST(debt_sum - NEW."ApprovedOffsetAmount", 0)
                 OR NEW."ExpectedRemainingDeposit" IS DISTINCT FROM
                    NEW."VerifiedHeldAmount" - NEW."ApprovedOffsetAmount"
                 OR encode(sha256(convert_to(NEW."ApprovalJson",'UTF8')),'hex')
                    IS DISTINCT FROM NEW."ApprovalSha256"
              THEN
                RAISE EXCEPTION 'Offset totals or proof checksum mismatch immutable official debt'
                  USING ERRCODE='23514';
              END IF;

              -- These approvals CANNOT be used as fully settled closeout:
              -- old no-offset ledger and COMMIT guards remain unchanged.
              RETURN NEW;
            END;
            $guard$;
            CREATE TRIGGER "TR_BM_OwnerDebtOffsetApprovalImmutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositDebtOffsetApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardOwnerDebtOffsetApproval"();

            CREATE FUNCTION "BM_RejectOwnerDebtOffsetApprovalTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $no_trunc$
            BEGIN
              RAISE EXCEPTION 'Owner offset approval history cannot be truncated'
                USING ERRCODE='23514';
            END;
            $no_trunc$;
            CREATE TRIGGER "TR_BM_OwnerDebtOffsetApprovalNoTruncate"
              BEFORE TRUNCATE
              ON "BuildingManagementContractDepositDebtOffsetApprovals"
              FOR EACH STATEMENT
              EXECUTE FUNCTION "BM_RejectOwnerDebtOffsetApprovalTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtOffsetApprovals"
              IN ACCESS EXCLUSIVE MODE;
            DO $history$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                  "BuildingManagementContractDepositDebtOffsetApprovals" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot erase signed Owner debt-offset approvals'
                  USING ERRCODE='23514';
              END IF;
            END;
            $history$;
            DROP TRIGGER "TR_BM_OwnerDebtOffsetApprovalNoTruncate"
              ON "BuildingManagementContractDepositDebtOffsetApprovals";
            DROP TRIGGER "TR_BM_OwnerDebtOffsetApprovalImmutable"
              ON "BuildingManagementContractDepositDebtOffsetApprovals";
            DROP FUNCTION "BM_RejectOwnerDebtOffsetApprovalTruncate"();
            DROP FUNCTION "BM_GuardOwnerDebtOffsetApproval"();
            DROP TABLE "BuildingManagementContractDepositDebtOffsetApprovals";
            """);
    }
}
