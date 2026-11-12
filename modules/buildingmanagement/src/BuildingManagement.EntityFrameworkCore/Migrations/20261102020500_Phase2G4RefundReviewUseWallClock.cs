using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// A signed review is created after transaction BEGIN. PostgreSQL now()
/// returns that BEGIN timestamp, not current wall-clock time, and falsely
/// rejects a valid review as future-dated. Keep serializable isolation
/// and source checks, but validate the attestation against clock_timestamp().
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102020500_Phase2G4RefundReviewUseWallClock")]
public sealed class Phase2G4RefundReviewUseWallClock : Migration
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
                 NEW."ReviewedAtUtc">clock_timestamp() OR
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
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositRefundBankReviews"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                "BuildingManagementContractDepositRefundBankReviews" LIMIT 1)
              THEN
                RAISE EXCEPTION
                  'Signed refund history prevents restoring a stale-transaction clock guard'
                  USING ERRCODE='23514';
              END IF;
            END; $down$;
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
            """);
    }
}
