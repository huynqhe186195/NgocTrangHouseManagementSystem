using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>2H D01/D02 verified, immutable per-request day rule; no money writer.</summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103070000_Phase2HDayProrationRuleCandidates")]
public sealed class Phase2HDayProrationRuleCandidates : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeDayPricingRuleCandidates" (
         "Id" uuid PRIMARY KEY,
         "RequestId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
         "SourceContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
         "SourceRoomId" uuid NOT NULL REFERENCES "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
         "TargetRoomId" uuid NOT NULL REFERENCES "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
         "BuildingId" uuid NOT NULL,
         "RosterSnapshotId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeTenantRosterSnapshots"("Id") ON DELETE RESTRICT,
         "PolicyId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeBusinessPolicies"("Id") ON DELETE RESTRICT,
         "PolicyReviewId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeBusinessPolicyReviews"("Id") ON DELETE RESTRICT,
         "TransferDate" date NOT NULL,
         "TransferDayOwner" integer NOT NULL,
         "Rounding" integer NOT NULL,
         "CalendarDays" integer NOT NULL,
         "SourceChargeableDays" integer NOT NULL,
         "TargetChargeableDays" integer NOT NULL,
         "SourceMonthlyRentVnd" numeric(18,0) NOT NULL,
         "TargetMonthlyRentVnd" numeric(18,0) NOT NULL,
         "ProposedSourceRentVnd" numeric(18,0) NOT NULL,
         "ProposedTargetRentVnd" numeric(18,0) NOT NULL,
         "SignedPricingRuleReference" varchar(500) NOT NULL,
         "PreparedByUserId" uuid NOT NULL,
         "PreparedAtUtc" timestamptz NOT NULL,
         "EvidenceSha256" varchar(64) NOT NULL,
         CONSTRAINT "CK_BM_RoomChangeDayPricingRule_Valid" CHECK (
          "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
          AND "SourceRoomId" <> "TargetRoomId"
          AND "TransferDayOwner" IN (1,2)
          AND "Rounding" IN (1,2)
          AND "CalendarDays" BETWEEN 28 AND 31
          AND "SourceChargeableDays" BETWEEN 0 AND 31
          AND "TargetChargeableDays" BETWEEN 0 AND 31
          AND "SourceChargeableDays"+"TargetChargeableDays"="CalendarDays"
          AND "SourceMonthlyRentVnd">0 AND "TargetMonthlyRentVnd">0
          AND "ProposedSourceRentVnd">=0 AND "ProposedTargetRentVnd">=0
          AND length(btrim("SignedPricingRuleReference")) BETWEEN 12 AND 500
          AND "PreparedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
          AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
         )
        );
        CREATE UNIQUE INDEX "UX_BM_RoomChangeDayPricingRule_Request"
         ON "BuildingManagementRoomChangeDayPricingRuleCandidates"("RequestId");
        
        CREATE FUNCTION "BM_CalculateRoomChangeDayPricingRuleSha"(
         row "BuildingManagementRoomChangeDayPricingRuleCandidates"
        ) RETURNS text LANGUAGE sql AS $sha$
         SELECT encode(sha256(convert_to(concat_ws(E'\n',
          pricing."Id"::text, pricing."RequestId"::text,
          pricing."SourceContractId"::text, pricing."SourceRoomId"::text,
          pricing."TargetRoomId"::text, pricing."BuildingId"::text,
          pricing."RosterSnapshotId"::text, pricing."PolicyId"::text,
          pricing."PolicyReviewId"::text,
          to_char(pricing."TransferDate"::timestamp,'YYYY-MM-DD'),
          pricing."TransferDayOwner"::text, pricing."Rounding"::text,
          pricing."CalendarDays"::text, pricing."SourceChargeableDays"::text,
          pricing."TargetChargeableDays"::text,
          pricing."SourceMonthlyRentVnd"::numeric(18,0)::text,
          pricing."TargetMonthlyRentVnd"::numeric(18,0)::text,
          pricing."ProposedSourceRentVnd"::numeric(18,0)::text,
          pricing."ProposedTargetRentVnd"::numeric(18,0)::text,
          pricing."SignedPricingRuleReference", pricing."PreparedByUserId"::text,
          to_char(pricing."PreparedAtUtc" AT TIME ZONE 'UTC',
            'YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z'
         ),'UTF8')),'hex');
        $sha$;
        
        CREATE FUNCTION "BM_GuardRoomChangeDayPricingRule"()
        RETURNS trigger LANGUAGE plpgsql AS $rule$
        DECLARE req record; contract record; roster record; policy record; review record;
                days integer; old_days integer; src numeric; dst numeric;
        BEGIN
         IF TG_OP <> 'INSERT' THEN
          RAISE EXCEPTION 'Day-proration choice is immutable' USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION 'Day-proration choice requires SERIALIZABLE' USING ERRCODE='23514';
         END IF;
         SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId" FOR SHARE;
         SELECT * INTO contract FROM "BuildingManagementContracts"
          WHERE "Id"=NEW."SourceContractId" FOR SHARE;
         SELECT * INTO roster FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
          WHERE "Id"=NEW."RosterSnapshotId" FOR SHARE;
         SELECT * INTO policy FROM "BuildingManagementRoomChangeBusinessPolicies"
          WHERE "Id"=NEW."PolicyId" FOR SHARE;
         SELECT * INTO review FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
          WHERE "Id"=NEW."PolicyReviewId" FOR SHARE;
        
         days := EXTRACT(day FROM (date_trunc('month',NEW."TransferDate"::timestamp)
           + interval '1 month - 1 day'))::integer;
         old_days := EXTRACT(day FROM NEW."TransferDate")::integer
           - CASE WHEN NEW."TransferDayOwner"=2 THEN 1 ELSE 0 END;
         src := NEW."SourceMonthlyRentVnd"::numeric*old_days/days;
         dst := NEW."TargetMonthlyRentVnd"::numeric*(days-old_days)/days;
         IF NEW."Rounding"=1 THEN
          src:=round(src,0); dst:=round(dst,0);
         ELSIF NEW."Rounding"=2 THEN
          src:=trunc(src,0); dst:=trunc(dst,0);
         ELSE
          RAISE EXCEPTION 'Unknown rounding policy' USING ERRCODE='23514';
         END IF;
        
         IF req."Id" IS NULL OR req."Status" NOT IN (0,1)
          OR req."RequestedDate" IS DISTINCT FROM NEW."TransferDate"
          OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
          OR req."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
          OR req."TargetRoomId" IS DISTINCT FROM NEW."TargetRoomId"
          OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
          OR contract."Id" IS NULL OR contract."Status" <> 2
          OR contract."RoomId" IS DISTINCT FROM NEW."SourceRoomId"
          OR contract."MonthlyRent" IS DISTINCT FROM NEW."SourceMonthlyRentVnd"
          OR contract."StartDate"::date > NEW."TransferDate"
          OR (contract."EndDate" IS NOT NULL AND
              contract."EndDate"::date < NEW."TransferDate")
          OR roster."Id" IS NULL OR roster."RosterCount" < 1
          OR roster."RoomChangeRequestId" IS DISTINCT FROM req."Id"
          OR roster."SourceContractId" IS DISTINCT FROM contract."Id"
          OR roster."SourceRoomId" IS DISTINCT FROM req."SourceRoomId"
          OR roster."BuildingId" IS DISTINCT FROM NEW."BuildingId"
          OR policy."Id" IS NULL OR policy."RequestId" IS DISTINCT FROM req."Id"
          OR policy."RosterSnapshotId" IS DISTINCT FROM roster."Id"
          OR policy."SourceContractId" IS DISTINCT FROM contract."Id"
          OR policy."BuildingId" IS DISTINCT FROM NEW."BuildingId"
          OR policy."ContractTreatment" <> 1
          OR policy."PricingTreatment" <> 2
          OR policy."TenantTreatment" <> 1
          OR policy."PricingPolicyReference" IS DISTINCT FROM NEW."SignedPricingRuleReference"
          OR review."Id" IS NULL OR review."Accepted" IS DISTINCT FROM TRUE
          OR review."PolicyId" IS DISTINCT FROM policy."Id"
          OR review."RequestId" IS DISTINCT FROM req."Id"
          OR review."BuildingId" IS DISTINCT FROM req."BuildingId"
          OR review."ProposalSha256" IS DISTINCT FROM policy."EvidenceSha256"
          OR review."ReviewedByUserId" = policy."ProposedByUserId"
          OR review."ReviewedByUserId" = NEW."PreparedByUserId"
          OR NEW."PreparedAtUtc" < review."ReviewedAtUtc"
          OR NEW."PreparedAtUtc" < req."CreatedAtUtc"
          OR NEW."CalendarDays" <> days
          OR NEW."SourceChargeableDays" <> old_days
          OR NEW."TargetChargeableDays" <> days-old_days
          OR NEW."ProposedSourceRentVnd" IS DISTINCT FROM src
          OR NEW."ProposedTargetRentVnd" IS DISTINCT FROM dst
          OR NEW."EvidenceSha256" IS DISTINCT FROM "BM_CalculateRoomChangeDayPricingRuleSha"(NEW)
         THEN
          RAISE EXCEPTION 'Day-proration candidate fails scoped pricing or SHA verification'
           USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $rule$;
        CREATE TRIGGER "TR_BM_RoomChangeDayPricingRule_Integrity"
         BEFORE INSERT OR UPDATE OR DELETE
         ON "BuildingManagementRoomChangeDayPricingRuleCandidates"
         FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeDayPricingRule"();
        
        CREATE FUNCTION "BM_NoTruncateRoomChangeDayPricingRules"()
        RETURNS trigger LANGUAGE plpgsql AS $retain$
        BEGIN
         RAISE EXCEPTION 'Pricing decision evidence cannot be truncated' USING ERRCODE='23514';
        END;
        $retain$;
        CREATE TRIGGER "TR_BM_RoomChangeDayPricingRule_NoTruncate"
         BEFORE TRUNCATE ON "BuildingManagementRoomChangeDayPricingRuleCandidates"
         FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangeDayPricingRules"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDayPricingRuleCandidates"
         IN ACCESS EXCLUSIVE MODE;
        DO $down$
        BEGIN
         IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" LIMIT 1) THEN
          RAISE EXCEPTION 'Refuse rollback of signed transfer pricing choices'
            USING ERRCODE='23514';
         END IF;
        END;
        $down$;
        DROP TRIGGER "TR_BM_RoomChangeDayPricingRule_NoTruncate"
         ON "BuildingManagementRoomChangeDayPricingRuleCandidates";
        DROP FUNCTION "BM_NoTruncateRoomChangeDayPricingRules"();
        DROP TRIGGER "TR_BM_RoomChangeDayPricingRule_Integrity"
         ON "BuildingManagementRoomChangeDayPricingRuleCandidates";
        DROP FUNCTION "BM_GuardRoomChangeDayPricingRule"();
        DROP FUNCTION "BM_CalculateRoomChangeDayPricingRuleSha"(
         "BuildingManagementRoomChangeDayPricingRuleCandidates");
        DROP TABLE "BuildingManagementRoomChangeDayPricingRuleCandidates";
        """);
}
