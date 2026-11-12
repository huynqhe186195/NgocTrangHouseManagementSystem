using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Durable independent manual-bank evidence before refund-closeout. Neither
/// this migration nor the review service sends funds or asserts bank-API
/// validation. An unreviewed Owner-entered reference is never enough.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101027000_Phase2G4IndependentRefundBankReview")]
public sealed class Phase2G4IndependentRefundBankReview : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE TABLE "BuildingManagementContractDepositRefundBankReviews" (
              "Id" uuid PRIMARY KEY,
              "ContractId" uuid NOT NULL REFERENCES
                 "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
              "MoveOutRequestId" uuid NOT NULL REFERENCES
                 "BuildingManagementMoveOutRequests"("Id") ON DELETE RESTRICT,
              "DepositDecisionId" uuid NOT NULL REFERENCES
                 "BuildingManagementContractDepositSettlementDecisions"("Id")
                 ON DELETE RESTRICT,
              "BuildingId" uuid NOT NULL,
              "ReviewedByUserId" uuid NOT NULL,
              "ReviewedAtUtc" timestamptz NOT NULL,
              "PaidAtUtc" timestamptz NOT NULL,
              "VerifiedRefundAmountVnd" numeric(18,0) NOT NULL,
              "BankTransactionReference" varchar(128) NOT NULL,
              "BankStatementEvidenceReference" varchar(500) NOT NULL,
              "SignedReviewReference" varchar(500) NOT NULL,
              "ReviewJson" text NOT NULL,
              "ReviewSha256" varchar(64) NOT NULL,
              CONSTRAINT "CK_BM_PostOffsetRefundReview_Proof" CHECK (
                "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ReviewedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "VerifiedRefundAmountVnd" > 0
                AND "VerifiedRefundAmountVnd"=trunc("VerifiedRefundAmountVnd")
                AND "PaidAtUtc"<="ReviewedAtUtc"
                AND length(btrim("BankTransactionReference")) BETWEEN 8 AND 128
                AND length(btrim("BankStatementEvidenceReference")) BETWEEN 12 AND 500
                AND length(btrim("SignedReviewReference")) BETWEEN 12 AND 500
                AND "BankTransactionReference" <> "BankStatementEvidenceReference"
                AND "BankTransactionReference" <> "SignedReviewReference"
                AND length("ReviewJson") BETWEEN 10 AND 1000000
                AND jsonb_typeof("ReviewJson"::jsonb)='object'
                AND "ReviewSha256" ~ '^[a-f0-9]{64}$')
            );
            CREATE UNIQUE INDEX "UX_BM_PostOffsetRefundReview_Contract"
              ON "BuildingManagementContractDepositRefundBankReviews"("ContractId");
            CREATE UNIQUE INDEX "UX_BM_PostOffsetRefundReview_Request"
              ON "BuildingManagementContractDepositRefundBankReviews"("MoveOutRequestId");
            CREATE UNIQUE INDEX "UX_BM_PostOffsetRefundReview_BankRef"
              ON "BuildingManagementContractDepositRefundBankReviews"
                ("BankTransactionReference");

            CREATE FUNCTION "BM_GuardIndependentPostOffsetRefundReview"()
            RETURNS trigger LANGUAGE plpgsql AS $refund_review$
            DECLARE decision record;
            DECLARE posted record;
            BEGIN
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'Signed independent refund bank review is immutable'
                  USING ERRCODE='23514';
              END IF;
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'Independent bank review requires SERIALIZABLE'
                  USING ERRCODE='23514';
              END IF;
              PERFORM 1 FROM "BuildingManagementContracts"
               WHERE "Id"=NEW."ContractId" FOR UPDATE;
              IF NOT FOUND THEN
                RAISE EXCEPTION 'Refund review has no locked contract'
                  USING ERRCODE='23514';
              END IF;
              SELECT * INTO decision
                FROM "BuildingManagementContractDepositSettlementDecisions"
               WHERE "Id"=NEW."DepositDecisionId"
                 AND "ContractId"=NEW."ContractId" FOR SHARE;
              SELECT * INTO posted
                FROM "BuildingManagementContractDepositDebtOffsetPostings"
               WHERE "ContractId"=NEW."ContractId";
              IF decision."Id" IS NULL OR posted."Id" IS NULL OR
                 decision."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId" OR
                 decision."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                 decision."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                    posted."DebitHeldDepositVnd" OR
                 decision."ApprovedRefundAmount" IS DISTINCT FROM
                    NEW."VerifiedRefundAmountVnd" OR
                 NEW."ReviewedByUserId"=decision."ApprovedByUserId" OR
                 NEW."PaidAtUtc"<decision."ApprovedAtUtc" OR
                 NEW."ReviewedAtUtc">now() OR
                 EXISTS (SELECT 1 FROM
                    "BuildingManagementContractDepositCloseoutApprovals" a
                   WHERE a."ContractId"=NEW."ContractId")
              THEN
                RAISE EXCEPTION 'Bank review does not match signed Owner refund and posted credit'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $refund_review$;
            CREATE TRIGGER "TR_BM_PostOffsetRefundReviewImmutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositRefundBankReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardIndependentPostOffsetRefundReview"();

            CREATE FUNCTION "BM_RequireIndependentRefundAtPostOffsetCloseout"()
            RETURNS trigger LANGUAGE plpgsql AS $review_closeout$
            DECLARE evidence record;
            DECLARE refund_notes text;
            BEGIN
              IF NEW."ApprovedDebtOffsetAmount" > 0 AND
                 NEW."ApprovedRefundAmount" > 0 THEN
                SELECT * INTO evidence
                  FROM "BuildingManagementContractDepositRefundBankReviews" r
                 WHERE r."ContractId"=NEW."ContractId"
                   AND r."MoveOutRequestId"=NEW."MoveOutRequestId"
                   AND r."VerifiedRefundAmountVnd"=NEW."ApprovedRefundAmount"
                   AND r."BankTransactionReference"=NEW."RefundTransactionReference"
                   AND r."PaidAtUtc"=NEW."RefundPaidAtUtc" FOR SHARE;
                SELECT s."Notes" INTO refund_notes
                  FROM "BuildingManagementDepositSettlements" s
                 WHERE s."Id"=NEW."RefundSettlementId";
                IF evidence."Id" IS NULL OR
                   evidence."ReviewedByUserId"=NEW."ApprovedByUserId" OR
                   evidence."ReviewedAtUtc"<evidence."PaidAtUtc" OR
                   refund_notes IS NULL OR
                   position(evidence."BankStatementEvidenceReference"
                     IN refund_notes)=0 OR
                   NOT EXISTS (
                     SELECT 1 FROM
                       "BuildingManagementContractDepositSettlementDecisions" d
                      WHERE d."Id"=evidence."DepositDecisionId"
                        AND d."ContractId"=NEW."ContractId"
                        AND d."ApprovedRefundAmount"=NEW."ApprovedRefundAmount"
                        AND d."ApprovedDebtOffsetAmount"=
                            NEW."ApprovedDebtOffsetAmount")
                THEN
                  RAISE EXCEPTION 'Post-offset executed refund lacks independent bank statement review'
                    USING ERRCODE='23514';
                END IF;
              ELSIF NEW."ApprovedDebtOffsetAmount" > 0 AND
                    NEW."ApprovedRefundAmount" = 0 AND
                    EXISTS (SELECT 1 FROM
                      "BuildingManagementContractDepositRefundBankReviews" r
                     WHERE r."ContractId"=NEW."ContractId")
              THEN
                RAISE EXCEPTION 'Zero refund must not reuse an executed bank review'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $review_closeout$;
            CREATE TRIGGER "TR_BM_01_PostOffsetRefundReviewCloseout"
              BEFORE INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_RequireIndependentRefundAtPostOffsetCloseout"();
            CREATE FUNCTION "BM_NoTruncatePostOffsetRefundReviews"()
            RETURNS trigger LANGUAGE plpgsql AS $no_trunc$
            BEGIN
              RAISE EXCEPTION 'Immutable bank refund proof cannot be truncated'
                USING ERRCODE='23514';
            END; $no_trunc$;
            CREATE TRIGGER "TR_BM_PostOffsetRefundReviewNoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementContractDepositRefundBankReviews"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncatePostOffsetRefundReviews"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositRefundBankReviews",
              "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                 "BuildingManagementContractDepositRefundBankReviews" LIMIT 1)
                 OR EXISTS (SELECT 1 FROM
                   "BuildingManagementContractDepositCloseoutApprovals"
                   WHERE "ApprovedDebtOffsetAmount">0)
              THEN
                RAISE EXCEPTION 'Signed post-offset refund evidence prevents rollback'
                  USING ERRCODE='23514';
              END IF;
            END; $down$;
            DROP TRIGGER "TR_BM_PostOffsetRefundReviewNoTruncate"
              ON "BuildingManagementContractDepositRefundBankReviews";
            DROP FUNCTION "BM_NoTruncatePostOffsetRefundReviews"();
            DROP TRIGGER "TR_BM_01_PostOffsetRefundReviewCloseout"
              ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP FUNCTION "BM_RequireIndependentRefundAtPostOffsetCloseout"();
            DROP TRIGGER "TR_BM_PostOffsetRefundReviewImmutable"
              ON "BuildingManagementContractDepositRefundBankReviews";
            DROP FUNCTION "BM_GuardIndependentPostOffsetRefundReview"();
            DROP TABLE "BuildingManagementContractDepositRefundBankReviews";
            """);
    }
}
