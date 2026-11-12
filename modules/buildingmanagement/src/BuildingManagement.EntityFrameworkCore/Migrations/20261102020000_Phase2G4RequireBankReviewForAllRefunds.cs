using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// No-offset full refunds must not be less trustworthy than net refunds
/// following an internal debt offset. This additive migration upgrades
/// BOTH PostgreSQL guards; it does not initiate bank transfers.
/// Manual review attests a statement, not live bank-API verification.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102020000_Phase2G4RequireBankReviewForAllRefunds")]
public sealed class Phase2G4RequireBankReviewForAllRefunds : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE OR REPLACE FUNCTION "BM_GuardIndependentPostOffsetRefundReview"()
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
              IF decision."Id" IS NULL OR
                 decision."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId" OR
                 decision."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                 decision."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                    COALESCE(posted."DebitHeldDepositVnd",0) OR
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
            CREATE OR REPLACE FUNCTION "BM_RequireIndependentRefundAtPostOffsetCloseout"()
            RETURNS trigger LANGUAGE plpgsql AS $review_closeout$
            DECLARE evidence record;
            DECLARE refund_notes text;
            BEGIN
              IF NEW."ApprovedRefundAmount" > 0 THEN
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
              ELSIF NEW."ApprovedRefundAmount" = 0 AND
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
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositRefundBankReviews",
                "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $rollback$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                  "BuildingManagementContractDepositRefundBankReviews" LIMIT 1)
                OR EXISTS (SELECT 1 FROM
                  "BuildingManagementContractDepositCloseoutApprovals"
                  WHERE "ApprovedRefundAmount" > 0)
              THEN
                RAISE EXCEPTION
                  'Cannot restore weaker manual refund review policy with signed history'
                  USING ERRCODE='23514';
              END IF;
            END;
            $rollback$;
            CREATE OR REPLACE FUNCTION "BM_GuardIndependentPostOffsetRefundReview"()
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
            CREATE OR REPLACE FUNCTION "BM_RequireIndependentRefundAtPostOffsetCloseout"()
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
            """);
    }
}
