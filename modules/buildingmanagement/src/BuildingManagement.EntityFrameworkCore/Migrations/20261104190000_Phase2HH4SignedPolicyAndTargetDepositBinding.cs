using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// New H4 documentary deposit proposals must match the independently
/// accepted policy and signed destination rent. Contract.DepositAmount
/// currently equals MonthlyRent, so a different deposit is not executable.
/// Does not post money or complete room transfers.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104190000_Phase2HH4SignedPolicyAndTargetDepositBinding")]
public sealed class Phase2HH4SignedPolicyAndTargetDepositBinding : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE FUNCTION "BM_2HH4IntentMatchesSignedPolicy"(
          intent "BuildingManagementRoomChangeDepositDispositionIntents"
        ) RETURNS boolean LANGUAGE plpgsql AS $check$
        DECLARE req record; p record; rv record; dayrule record;
                expected_treatment integer;
        BEGIN
          SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
           WHERE "Id"=intent."RequestId";
          SELECT * INTO p FROM "BuildingManagementRoomChangeBusinessPolicies"
           WHERE "RequestId"=intent."RequestId";
          SELECT * INTO rv FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
           WHERE "PolicyId"=p."Id";
          SELECT * INTO dayrule FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
           WHERE "RequestId"=intent."RequestId";

          expected_treatment := CASE
            WHEN intent."ProposedUnpaidTopUpVnd">0 THEN 2
            WHEN intent."ProposedFutureRentCreditVnd">0
              OR intent."ProposedManualRefundVnd">0 THEN 3
            ELSE -1
          END;

          RETURN req."Id" IS NOT NULL AND p."Id" IS NOT NULL
             AND rv."Id" IS NOT NULL AND dayrule."Id" IS NOT NULL
             AND req."SourceContractId" = intent."SourceContractId"
             AND req."BuildingId" = intent."BuildingId"
             AND req."RequestedDate" = intent."TransferDateVietnam"
             AND p."SourceContractId" = req."SourceContractId"
             AND p."BuildingId" = req."BuildingId"
             AND p."ContractTreatment" = 1
             AND p."PricingTreatment" = 2
             AND p."TenantTreatment" = 1
             AND p."DepositTreatment" = expected_treatment
             AND p."DepositPolicyReference" =
                 intent."TargetDepositPolicyReference"
             AND rv."Accepted" IS TRUE
             AND rv."RequestId" = req."Id"
             AND rv."BuildingId" = req."BuildingId"
             AND rv."ProposalSha256" = p."EvidenceSha256"
             AND rv."ReviewedByUserId" <> p."ProposedByUserId"
             AND rv."ReviewedByUserId" = intent."ProposedByOwnerId"
             AND dayrule."SourceContractId" = req."SourceContractId"
             AND dayrule."BuildingId" = req."BuildingId"
             AND dayrule."PolicyId" = p."Id"
             AND dayrule."PolicyReviewId" = rv."Id"
             AND dayrule."TransferDate" = req."RequestedDate"
             AND dayrule."TransferDayOwner" = 2
             AND dayrule."Rounding" = 1
             AND dayrule."SignedPricingRuleReference" =
                 p."PricingPolicyReference"
             AND dayrule."TargetMonthlyRentVnd" =
                 intent."TargetRequiredDepositVnd"
             AND intent."ProposedAtUtc" >= dayrule."PreparedAtUtc"
             AND intent."ProposedAtUtc" >= rv."ReviewedAtUtc";
        END;
        $check$;

        -- Fail closed on incompatible history. Never silently rewrite a
        -- signed original, nor accept an unenforceable legacy decision.
        DO $history$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeDepositDispositionIntents" d
             WHERE "BM_2HH4IntentMatchesSignedPolicy"(d) IS DISTINCT FROM TRUE
          ) THEN
            RAISE EXCEPTION 'Existing H4 intent contradicts original signed policy'
              USING ERRCODE='23514';
          END IF;
        END;
        $history$;

        CREATE FUNCTION "BM_2HVerifyH4IntentSignedPolicy"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
          IF current_setting('transaction_isolation') <> 'serializable'
             OR "BM_2HH4IntentMatchesSignedPolicy"(NEW) IS DISTINCT FROM TRUE
          THEN
            RAISE EXCEPTION 'H4 intent lacks matching signed policy and target price'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $guard$;
        CREATE TRIGGER "TR_BM_2H_H4Intent_SignedPolicy"
          BEFORE INSERT
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HVerifyH4IntentSignedPolicy"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDepositDispositionIntents"
          IN ACCESS EXCLUSIVE MODE;
        DO $retain$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeDepositDispositionIntents"
             LIMIT 1
          ) THEN
            RAISE EXCEPTION 'Cannot downgrade signed H4 policy binding with evidence'
              USING ERRCODE='23514';
          END IF;
        END;
        $retain$;
        DROP TRIGGER "TR_BM_2H_H4Intent_SignedPolicy"
          ON "BuildingManagementRoomChangeDepositDispositionIntents";
        DROP FUNCTION "BM_2HVerifyH4IntentSignedPolicy"();
        DROP FUNCTION "BM_2HH4IntentMatchesSignedPolicy"(
          "BuildingManagementRoomChangeDepositDispositionIntents");
        """);
}
