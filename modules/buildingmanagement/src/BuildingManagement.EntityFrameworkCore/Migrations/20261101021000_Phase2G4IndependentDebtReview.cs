using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Stage 2G.4 independent debt verifier: immutable signed evidence, separate
/// from the ROOT authorization, and required by the Owner approval SQL
/// BEFORE INSERT. Neither table can post cash or debt-settle a contract.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101021000_Phase2G4IndependentDebtReview")]
public sealed class Phase2G4IndependentDebtReview : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE TABLE "BuildingManagementContractDepositDebtReviews" (
              "Id" uuid PRIMARY KEY,
              "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
              "MoveOutRequestId" uuid NOT NULL REFERENCES "BuildingManagementMoveOutRequests"("Id") ON DELETE RESTRICT,
              "BuildingId" uuid NOT NULL,
              "ReviewedByUserId" uuid NOT NULL,
              "ReviewedAsOfDate" date NOT NULL,
              "ReviewedAtUtc" timestamptz NOT NULL,
              "VerifiedHeldAmount" numeric(18,0) NOT NULL,
              "VerifiedOutstandingAmount" numeric(18,0) NOT NULL,
              "SignedReviewReference" varchar(500) NOT NULL,
              "ReviewJson" text NOT NULL,
              "ReviewSha256" varchar(64) NOT NULL,
              CONSTRAINT "CK_BM_OffsetReview_Valid" CHECK (
                "ReviewedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "VerifiedHeldAmount" > 0
                AND "VerifiedOutstandingAmount" > 0
                AND length(btrim("SignedReviewReference")) BETWEEN 12 AND 500
                AND "ReviewSha256" ~ '^[0-9a-f]{64}$'
                AND length("ReviewJson") BETWEEN 100 AND 1000000
                AND jsonb_typeof("ReviewJson"::jsonb)='object'
              )
            );
            CREATE UNIQUE INDEX "UX_BM_OffsetReview_Contract"
              ON "BuildingManagementContractDepositDebtReviews"("ContractId");
            CREATE UNIQUE INDEX "UX_BM_OffsetReview_Request"
              ON "BuildingManagementContractDepositDebtReviews"("MoveOutRequestId");

            CREATE FUNCTION "BM_GuardIndependentDebtReview"()
            RETURNS trigger LANGUAGE plpgsql AS $review$
            DECLARE contract_row record;
            DECLARE request_row record;
            DECLARE proof jsonb;
            BEGIN
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'Independent debt review is append-only'
                  USING ERRCODE='23514';
              END IF;
              IF current_setting('transaction_isolation') <> 'serializable' THEN
                RAISE EXCEPTION 'Independent debt review requires SERIALIZABLE'
                  USING ERRCODE='23514';
              END IF;
              SELECT "Id","RoomId","Status","DepositAmount"
                INTO contract_row FROM "BuildingManagementContracts"
                WHERE "Id"=NEW."ContractId" FOR UPDATE;
              SELECT "Id","ContractId","RoomId","Status"
                INTO request_row FROM "BuildingManagementMoveOutRequests"
                WHERE "Id"=NEW."MoveOutRequestId" FOR SHARE;
              IF contract_row."Id" IS NULL OR
                 request_row."Id" IS NULL OR
                 contract_row."Status" IS DISTINCT FROM 2 OR
                 request_row."Status" IS DISTINCT FROM 1 OR
                 request_row."ContractId" IS DISTINCT FROM contract_row."Id" OR
                 request_row."RoomId" IS DISTINCT FROM contract_row."RoomId" OR
                 NEW."VerifiedHeldAmount" IS DISTINCT FROM
                     contract_row."DepositAmount" OR
                 NEW."ReviewedAtUtc" > clock_timestamp()
              THEN
                RAISE EXCEPTION 'Independent debt review not scoped to valid active move-out'
                  USING ERRCODE='23514';
              END IF;
              proof:=NEW."ReviewJson"::jsonb;
              IF (proof->>'ContractId')::uuid IS DISTINCT FROM NEW."ContractId" OR
                 (proof->>'MoveOutRequestId')::uuid IS DISTINCT FROM NEW."MoveOutRequestId" OR
                 (proof->>'BuildingId')::uuid IS DISTINCT FROM NEW."BuildingId" OR
                 (proof->>'ReviewedByUserId')::uuid IS DISTINCT FROM NEW."ReviewedByUserId" OR
                 (proof->>'ReviewedAsOfDate')::date IS DISTINCT FROM NEW."ReviewedAsOfDate" OR
                 (proof->>'VerifiedHeldAmount')::numeric IS DISTINCT FROM NEW."VerifiedHeldAmount" OR
                 (proof->>'VerifiedOutstandingAmount')::numeric IS DISTINCT FROM NEW."VerifiedOutstandingAmount" OR
                 proof->>'SignedReviewReference' IS DISTINCT FROM NEW."SignedReviewReference" OR
                 jsonb_typeof(proof->'Claims') IS DISTINCT FROM 'array' OR
                 encode(sha256(convert_to(NEW."ReviewJson",'UTF8')),'hex')
                     IS DISTINCT FROM NEW."ReviewSha256"
              THEN
                RAISE EXCEPTION 'Independent review immutable proof invalid'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $review$;

            CREATE TRIGGER "TR_BM_IndependentDebtReviewImmutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositDebtReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardIndependentDebtReview"();

            -- A direct INSERT of an Owner authorization cannot invent an
            -- independent staff review by merely supplying an arbitrary ID.
            -- BOTH snapshots must describe exactly the same source claims.
            CREATE FUNCTION "BM_RequireIndependentDebtReviewForOwner"()
            RETURNS trigger LANGUAGE plpgsql AS $require$
            DECLARE r record;
            BEGIN
              SELECT * INTO r
                FROM "BuildingManagementContractDepositDebtReviews"
                WHERE "ContractId"=NEW."ContractId"
                  AND "MoveOutRequestId"=NEW."MoveOutRequestId"
                FOR SHARE;
              IF r."Id" IS NULL OR
                 r."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                 r."ReviewedByUserId" IS DISTINCT FROM NEW."IndependentDebtReviewerId" OR
                 r."SignedReviewReference" IS DISTINCT FROM NEW."IndependentReviewReference" OR
                 r."VerifiedHeldAmount" IS DISTINCT FROM NEW."VerifiedHeldAmount" OR
                 r."ReviewedAtUtc" > NEW."ApprovedAtUtc" OR
                 r."VerifiedOutstandingAmount" IS DISTINCT FROM
                   (SELECT COALESCE(sum((c->>'OutstandingVnd')::numeric),0)
                    FROM jsonb_array_elements(NEW."ApprovalJson"::jsonb->'Claims') c) OR
                 r."ReviewJson"::jsonb->'Claims' IS DISTINCT FROM
                     NEW."ApprovalJson"::jsonb->'Claims'
              THEN
                RAISE EXCEPTION 'Owner offset authorization requires matching independently committed review'
                  USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END;
            $require$;
            CREATE TRIGGER "TR_BM_00_RequireIndependentDebtReviewForOwner"
              BEFORE INSERT ON "BuildingManagementContractDepositDebtOffsetApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_RequireIndependentDebtReviewForOwner"();

            CREATE FUNCTION "BM_RejectDebtReviewTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $no_trunc$
            BEGIN
              RAISE EXCEPTION 'Signed debt review history cannot be truncated'
                USING ERRCODE='23514';
            END;
            $no_trunc$;
            CREATE TRIGGER "TR_BM_DebtReviewNoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementContractDepositDebtReviews"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_RejectDebtReviewTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtReviews",
                       "BuildingManagementContractDepositDebtOffsetApprovals"
              IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                   "BuildingManagementContractDepositDebtReviews" LIMIT 1) OR
                 EXISTS (SELECT 1 FROM
                   "BuildingManagementContractDepositDebtOffsetApprovals" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot downgrade independently signed financial reviews'
                  USING ERRCODE='23514';
              END IF;
            END;
            $guard$;
            DROP TRIGGER "TR_BM_DebtReviewNoTruncate"
              ON "BuildingManagementContractDepositDebtReviews";
            DROP TRIGGER "TR_BM_00_RequireIndependentDebtReviewForOwner"
              ON "BuildingManagementContractDepositDebtOffsetApprovals";
            DROP TRIGGER "TR_BM_IndependentDebtReviewImmutable"
              ON "BuildingManagementContractDepositDebtReviews";
            DROP FUNCTION "BM_RejectDebtReviewTruncate"();
            DROP FUNCTION "BM_RequireIndependentDebtReviewForOwner"();
            DROP FUNCTION "BM_GuardIndependentDebtReview"();
            DROP TABLE "BuildingManagementContractDepositDebtReviews";
            """);
    }
}
