using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Experimental staging H4 completion extension with exact immutable
/// outstanding deposit receivable OR exclusive next-rent credit/refund
/// liability, never claimed as externally bank-settled cash.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104200000_Phase2HAtomicH4DepositObligations")]
public sealed class Phase2HAtomicH4DepositObligations : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRoomChangeMidMonthCompletions"
          ADD COLUMN "H4DispositionIntentId" uuid NULL REFERENCES
            "BuildingManagementRoomChangeDepositDispositionIntents"("Id") ON DELETE RESTRICT,
          ADD COLUMN "H4IntentSha256" varchar(64) NULL,
          ADD COLUMN "H4OriginalHeldDepositVnd" numeric(18,0) NOT NULL DEFAULT 0,
          ADD COLUMN "H4TargetRequiredDepositVnd" numeric(18,0) NOT NULL DEFAULT 0,
          ADD COLUMN "H4UnpaidTopUpVnd" numeric(18,0) NOT NULL DEFAULT 0,
          ADD COLUMN "H4TopUpDueDateVietnam" date NULL,
          ADD COLUMN "H4NextMonthRentCreditVnd" numeric(18,0) NOT NULL DEFAULT 0,
          ADD COLUMN "H4ManualRefundLiabilityVnd" numeric(18,0) NOT NULL DEFAULT 0;
        CREATE UNIQUE INDEX "UX_BM_2HMidComplete_H4Intent"
          ON "BuildingManagementRoomChangeMidMonthCompletions"
            ("H4DispositionIntentId") WHERE "H4DispositionIntentId" IS NOT NULL;
        ALTER TABLE "BuildingManagementRoomChangeMidMonthCompletions"
          ADD CONSTRAINT "CK_BM_2H_H4MoneyShape" CHECK (
            ("H4DispositionIntentId" IS NULL
             AND "H4IntentSha256" IS NULL
             AND "H4OriginalHeldDepositVnd"=0
             AND "H4TargetRequiredDepositVnd"=0
             AND "H4UnpaidTopUpVnd"=0
             AND "H4TopUpDueDateVietnam" IS NULL
             AND "H4NextMonthRentCreditVnd"=0
             AND "H4ManualRefundLiabilityVnd"=0)
            OR
            ("H4DispositionIntentId" IS NOT NULL
             AND "H4IntentSha256"~'^[0-9a-f]{64}$'
             AND "H4OriginalHeldDepositVnd">0
             AND "H4TargetRequiredDepositVnd">0
             AND "H4UnpaidTopUpVnd">=0
             AND "H4NextMonthRentCreditVnd">=0
             AND "H4ManualRefundLiabilityVnd">=0
             AND "H4OriginalHeldDepositVnd" =
               "CarriedDepositVnd"+"H4NextMonthRentCreditVnd"
                 +"H4ManualRefundLiabilityVnd"
             AND "H4TargetRequiredDepositVnd" =
               "CarriedDepositVnd"+"H4UnpaidTopUpVnd"
             AND (("H4UnpaidTopUpVnd">0 AND "H4TopUpDueDateVietnam" IS NOT NULL
                   AND "H4NextMonthRentCreditVnd"=0
                   AND "H4ManualRefundLiabilityVnd"=0)
               OR ("H4UnpaidTopUpVnd"=0
                   AND "H4TopUpDueDateVietnam" IS NULL
                   AND (("H4NextMonthRentCreditVnd">0
                         AND "H4ManualRefundLiabilityVnd"=0)
                     OR ("H4ManualRefundLiabilityVnd">0
                         AND "H4NextMonthRentCreditVnd"=0)))))
          );

        CREATE TABLE "BuildingManagementRoomChangeH4Obligations" (
          "Id" uuid PRIMARY KEY,
          "CompletionId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeMidMonthCompletions"("Id") ON DELETE RESTRICT,
          "IntentId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeDepositDispositionIntents"("Id") ON DELETE RESTRICT,
          "RequestId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
          "SourceContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "TargetContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "Kind" integer NOT NULL,
          "AmountVnd" numeric(18,0) NOT NULL,
          "DueDateVietnam" date NULL,
          "RecordedByOwnerId" uuid NOT NULL,
          "RecordedAtUtc" timestamptz NOT NULL,
          "SignedReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_2H_H4Obligation" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "Kind" IN (1,2,3)
            AND "AmountVnd">0
            AND "RecordedByOwnerId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("SignedReference")) BETWEEN 12 AND 500
            AND (("Kind"=1 AND "DueDateVietnam" IS NOT NULL)
              OR ("Kind" IN (2,3) AND "DueDateVietnam" IS NULL))
          )
        );
        CREATE UNIQUE INDEX "UX_BM_H4Obligation_Completion"
          ON "BuildingManagementRoomChangeH4Obligations"("CompletionId");
        CREATE UNIQUE INDEX "UX_BM_H4Obligation_Intent"
          ON "BuildingManagementRoomChangeH4Obligations"("IntentId");
        CREATE UNIQUE INDEX "UX_BM_H4Obligation_Source"
          ON "BuildingManagementRoomChangeH4Obligations"("SourceContractId");
        CREATE UNIQUE INDEX "UX_BM_H4Obligation_Request"
          ON "BuildingManagementRoomChangeH4Obligations"("RequestId");

        CREATE FUNCTION "BM_2HGuardH4Obligation"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE c record; original record;
        BEGIN
          IF TG_OP <> 'INSERT' OR
             current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'H4 obligations immutable and require SERIALIZABLE'
              USING ERRCODE='23514';
          END IF;
          SELECT * INTO c
            FROM "BuildingManagementRoomChangeMidMonthCompletions"
           WHERE "Id"=NEW."CompletionId" FOR SHARE;
          SELECT * INTO original
            FROM "BuildingManagementRoomChangeDepositDispositionIntents"
           WHERE "Id"=NEW."IntentId" FOR SHARE;
          IF c."Id" IS NULL OR original."Id" IS NULL
             OR c."H4DispositionIntentId" IS DISTINCT FROM original."Id"
             OR c."RequestId" IS DISTINCT FROM NEW."RequestId"
             OR c."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR c."TargetContractId" IS DISTINCT FROM NEW."TargetContractId"
             OR c."CompletedByUserId" IS DISTINCT FROM NEW."RecordedByOwnerId"
             OR c."CompletedAtUtc" IS DISTINCT FROM NEW."RecordedAtUtc"
             OR original."RequestId" IS DISTINCT FROM NEW."RequestId"
             OR original."TargetContractId" IS DISTINCT FROM NEW."TargetContractId"
             OR original."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR NEW."SignedReference" IS DISTINCT FROM
                 original."OwnerSignedDispositionReference"
             OR NEW."Kind" IS DISTINCT FROM
               (CASE WHEN c."H4UnpaidTopUpVnd">0 THEN 1
                     WHEN c."H4NextMonthRentCreditVnd">0 THEN 2
                     ELSE 3 END)
             OR NEW."AmountVnd" IS DISTINCT FROM
               (CASE WHEN c."H4UnpaidTopUpVnd">0 THEN c."H4UnpaidTopUpVnd"
                     WHEN c."H4NextMonthRentCreditVnd">0
                       THEN c."H4NextMonthRentCreditVnd"
                     ELSE c."H4ManualRefundLiabilityVnd" END)
             OR NEW."DueDateVietnam" IS DISTINCT FROM c."H4TopUpDueDateVietnam"
          THEN
            RAISE EXCEPTION 'H4 obligation must match original signed atomic completion'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $guard$;
        CREATE TRIGGER "TR_BM_2H_H4Obligation_Immutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeH4Obligations"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HGuardH4Obligation"();
        CREATE FUNCTION "BM_2HNoTruncateH4Obligation"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
          RAISE EXCEPTION 'Signed H4 obligations cannot be truncated'
            USING ERRCODE='23514';
        END;
        $guard$;
        CREATE TRIGGER "TR_BM_2H_H4Obligation_NoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomChangeH4Obligations"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_2HNoTruncateH4Obligation"();

        CREATE FUNCTION "BM_Calculate2HMidCompletionSha"(
          h "BuildingManagementRoomChangeMidMonthCompletions"
        ) RETURNS text LANGUAGE sql AS $sha$
          SELECT encode(sha256(convert_to(
            concat_ws(E'\n',
               h."Id"::text,h."RequestId"::text,
               h."SourceContractId"::text,h."TargetContractId"::text,
               h."SourceRoomId"::text,h."TargetRoomId"::text,
               h."BuildingId"::text,h."PolicyId"::text,
               h."PolicyReviewId"::text,h."RosterSnapshotId"::text,
               h."HandoverEvidenceId"::text,h."PricingArchiveId"::text,
               h."ThreePartyArchiveId"::text,h."PricingRuleId"::text,
               h."SourceMonthlyInvoiceId"::text,h."SourceReceivableId"::text,
               h."SourceSignedAdjustmentVnd"::text,
               h."SourceAdjustedReceivableVnd"::text,
               h."SourceReviewedReceiptsVnd"::text,
               h."TargetFirstMonthRentVnd"::text,
               h."CarryForwardSettlementId"::text,
               h."CarriedDepositVnd"::text,
               h."SourceRoomDisposition"::text,
               h."DispositionEvidenceReference",
               h."CompletionReference",
               h."CompletedByUserId"::text,
               to_char(h."CompletedAtUtc" AT TIME ZONE 'UTC',
                       'YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z'
             )
            || CASE WHEN h."H4DispositionIntentId" IS NULL THEN ''
              ELSE E'\n' || concat_ws(E'\n',
                h."H4DispositionIntentId"::text,
                h."H4IntentSha256",
                h."H4OriginalHeldDepositVnd"::text,
                h."H4TargetRequiredDepositVnd"::text,
                h."H4UnpaidTopUpVnd"::text,
                coalesce(to_char(h."H4TopUpDueDateVietnam"::timestamp,'YYYY-MM-DD'),''),
                h."H4NextMonthRentCreditVnd"::text,
                h."H4ManualRefundLiabilityVnd"::text)
              END
          ,'UTF8')),'hex');
        $sha$;

        CREATE OR REPLACE FUNCTION "BM_Verify2HMidMonthCommit"()
        RETURNS trigger LANGUAGE plpgsql AS $mid$
        DECLARE req record; intent record; obligation record; p record; rv record; roster record; handover record;
                source record; target record; carry record; day_rule record;
                pricing record; signed record; invoice record; candidate record;
                receivable record; original_net numeric; corrected numeric;
                paid numeric; due_other bigint; correction_total numeric;
                target_status integer; source_status integer; original_held numeric;
                bank_claims bigint; original_archive jsonb; handover_json jsonb;
                billing_period date;
        BEGIN
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION 'Mid-month commit must be SERIALIZABLE' USING ERRCODE='23514';
         END IF;
        
         SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId";
         SELECT * INTO p FROM "BuildingManagementRoomChangeBusinessPolicies"
          WHERE "Id"=NEW."PolicyId";
         SELECT * INTO rv FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
          WHERE "Id"=NEW."PolicyReviewId";
         SELECT * INTO roster FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
          WHERE "Id"=NEW."RosterSnapshotId";
         SELECT * INTO handover FROM "BuildingManagementRoomChangeHandoverEvidence"
          WHERE "Id"=NEW."HandoverEvidenceId";
         SELECT * INTO day_rule FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
          WHERE "Id"=NEW."PricingRuleId";
         SELECT * INTO pricing FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."PricingArchiveId";
         SELECT * INTO signed FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."ThreePartyArchiveId";
         SELECT * INTO source FROM "BuildingManagementContracts"
          WHERE "Id"=NEW."SourceContractId";
         SELECT * INTO target FROM "BuildingManagementContracts"
          WHERE "Id"=NEW."TargetContractId";
         SELECT * INTO carry FROM "BuildingManagementDepositSettlements"
          WHERE "Id"=NEW."CarryForwardSettlementId";
         SELECT * INTO intent FROM "BuildingManagementRoomChangeDepositDispositionIntents"
          WHERE "Id"=NEW."H4DispositionIntentId";
         SELECT * INTO invoice FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
          WHERE "Id"=NEW."SourceMonthlyInvoiceId";
         SELECT * INTO receivable FROM "BuildingManagementOfficialInvoiceReceivables"
          WHERE "Id"=NEW."SourceReceivableId";
         SELECT * INTO candidate FROM "BuildingManagementMonthlyInvoiceCandidates"
          WHERE "Id"=invoice."CandidateId";
         SELECT "Status" INTO source_status FROM "BuildingManagementRooms"
          WHERE "Id"=NEW."SourceRoomId";
         SELECT "Status" INTO target_status FROM "BuildingManagementRooms"
          WHERE "Id"=NEW."TargetRoomId";
        
         IF req."Id" IS NULL THEN
          RAISE EXCEPTION 'Missing transfer request' USING ERRCODE='23514';
         END IF;
         billing_period:=make_date(extract(year from req."RequestedDate")::integer,
                                   extract(month from req."RequestedDate")::integer,1);
        
         IF req."Status"<>4
           OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
           OR req."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
           OR req."TargetRoomId" IS DISTINCT FROM NEW."TargetRoomId"
           OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
           OR req."ReviewedByUserId" IS DISTINCT FROM NEW."CompletedByUserId"
           OR req."ReviewedAtUtc" IS DISTINCT FROM NEW."CompletedAtUtc"
           OR req."ReviewNotes" IS DISTINCT FROM NEW."CompletionReference"
           OR req."RequestedDate" =
              (date_trunc('month',req."RequestedDate"::timestamp)
                  + interval '1 month - 1 day')::date
           OR NEW."CompletedAtUtc" > clock_timestamp()
           OR (NEW."CompletedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                < req."RequestedDate"
           OR p."Id" IS NULL OR p."RequestId" IS DISTINCT FROM req."Id"
           OR p."SourceContractId" IS DISTINCT FROM source."Id"
           OR p."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR p."ContractTreatment"<>1 OR p."PricingTreatment"<>2
           OR (NEW."H4DispositionIntentId" IS NULL AND p."DepositTreatment"<>1)
           OR (NEW."H4DispositionIntentId" IS NOT NULL AND p."DepositTreatment" NOT IN (2,3)) OR p."TenantTreatment"<>1
           OR rv."Id" IS NULL OR rv."Accepted" IS DISTINCT FROM TRUE
           OR rv."PolicyId" IS DISTINCT FROM p."Id"
           OR rv."RequestId" IS DISTINCT FROM req."Id"
           OR rv."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR rv."ProposalSha256" IS DISTINCT FROM p."EvidenceSha256"
           OR rv."ReviewedByUserId"=p."ProposedByUserId"
           OR roster."Id" IS NULL OR p."RosterSnapshotId" IS DISTINCT FROM roster."Id"
           OR p."RosterSha256" IS DISTINCT FROM roster."RosterSha256"
           OR roster."RoomChangeRequestId" IS DISTINCT FROM req."Id"
           OR handover."Id" IS NULL OR handover."RequestId" IS DISTINCT FROM req."Id"
           OR handover."SourceContractId" IS DISTINCT FROM source."Id"
           OR handover."SourceRoomId" IS DISTINCT FROM req."SourceRoomId"
           OR handover."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR handover."HandoverDate" IS DISTINCT FROM req."RequestedDate"
           OR day_rule."Id" IS NULL OR day_rule."RequestId" IS DISTINCT FROM req."Id"
           OR day_rule."SourceContractId" IS DISTINCT FROM source."Id"
           OR day_rule."PolicyId" IS DISTINCT FROM p."Id"
           OR day_rule."PolicyReviewId" IS DISTINCT FROM rv."Id"
           OR day_rule."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR day_rule."TransferDate" IS DISTINCT FROM req."RequestedDate"
           OR day_rule."SourceChargeableDays"<=0
           OR day_rule."TargetChargeableDays"<=0
           OR day_rule."CalendarDays" IS DISTINCT FROM
              day_rule."SourceChargeableDays"+day_rule."TargetChargeableDays"
           OR pricing."Id" IS NULL OR pricing."Kind"<>1
           OR pricing."RequestId" IS DISTINCT FROM req."Id"
           OR pricing."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR pricing."ParentEvidenceId" IS DISTINCT FROM day_rule."Id"
           OR pricing."ParentEvidenceSha256" IS DISTINCT FROM day_rule."EvidenceSha256"
           OR signed."Id" IS NULL OR signed."Kind"<>4
           OR signed."RequestId" IS DISTINCT FROM req."Id"
           OR signed."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR signed."ParentEvidenceId" IS DISTINCT FROM handover."Id"
           OR signed."ParentEvidenceSha256" IS DISTINCT FROM handover."EvidenceSha256"
           OR source."Id" IS NULL OR source."Status"<>3 OR source."EndReason"<>4
           OR source."ActualEndedAt" IS NULL
           OR source."RoomId" IS DISTINCT FROM req."SourceRoomId"
           OR target."Id" IS NULL OR target."Status"<>2
           OR target."RoomId" IS DISTINCT FROM req."TargetRoomId"
           OR target."RenewedFromContractId" IS DISTINCT FROM source."Id"
           OR target."StartDate"::date IS DISTINCT FROM
               (billing_period + day_rule."SourceChargeableDays")
           OR (NEW."H4DispositionIntentId" IS NULL
                AND source."MonthlyRent" IS DISTINCT FROM target."MonthlyRent")
           OR source."MonthlyRent" IS DISTINCT FROM day_rule."SourceMonthlyRentVnd"
           OR target."MonthlyRent" IS DISTINCT FROM day_rule."TargetMonthlyRentVnd"
           OR source."DepositAmount"<=0
           OR (NEW."H4DispositionIntentId" IS NULL
                AND target."DepositAmount" IS DISTINCT FROM source."DepositAmount")
           OR target_status<>2 OR source_status IS DISTINCT FROM NEW."SourceRoomDisposition"
           OR NEW."TargetFirstMonthRentVnd" IS DISTINCT FROM day_rule."ProposedTargetRentVnd"
           OR carry."Id" IS NULL OR carry."Type"<>5 OR carry."Status"<>2
           OR carry."SourceContractId" IS DISTINCT FROM source."Id"
           OR carry."TargetContractId" IS DISTINCT FROM target."Id"
           OR (NEW."H4DispositionIntentId" IS NULL
                AND carry."Amount" IS DISTINCT FROM source."DepositAmount")
           OR NEW."CarriedDepositVnd" IS DISTINCT FROM carry."Amount"
           OR carry."CompletedAt" IS NULL
           OR EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeCompletions"
              WHERE "SourceContractId"=NEW."SourceContractId" OR "RequestId"=NEW."RequestId")
         THEN
          RAISE EXCEPTION 'Mid-month atomic completion contradicts approved H1–H6 scope'
           USING ERRCODE='23514';
         END IF;
        
         -- H4 is a future obligation, not an externally verified bank
         -- receipt. It must be posted exactly once with this completion.
         IF NEW."H4DispositionIntentId" IS NOT NULL THEN
           IF intent."Id" IS NULL
             OR intent."RequestId" IS DISTINCT FROM req."Id"
             OR intent."SourceContractId" IS DISTINCT FROM source."Id"
             OR intent."TargetContractId" IS DISTINCT FROM target."Id"
             OR intent."BuildingId" IS DISTINCT FROM req."BuildingId"
             OR intent."TransferDateVietnam" IS DISTINCT FROM req."RequestedDate"
             OR intent."EvidenceSha256" IS DISTINCT FROM NEW."H4IntentSha256"
             OR intent."ProposedByOwnerId" IS DISTINCT FROM NEW."CompletedByUserId"
             OR NEW."CompletedAtUtc" < intent."ProposedAtUtc"
             OR intent."AlreadyPostedApprovedDeductionsVnd" <> 0
             OR intent."ClaimedOriginalSourceHeldVnd" IS DISTINCT FROM source."DepositAmount"
             OR intent."ClaimedOriginalSourceHeldVnd" IS DISTINCT FROM NEW."H4OriginalHeldDepositVnd"
             OR intent."TargetRequiredDepositVnd" IS DISTINCT FROM target."DepositAmount"
             OR intent."TargetRequiredDepositVnd" IS DISTINCT FROM NEW."H4TargetRequiredDepositVnd"
             OR intent."ProposedCarryTargetVnd" IS DISTINCT FROM carry."Amount"
             OR intent."ProposedUnpaidTopUpVnd" IS DISTINCT FROM NEW."H4UnpaidTopUpVnd"
             OR intent."ProposedTopUpDueDateVietnam"
                  IS DISTINCT FROM NEW."H4TopUpDueDateVietnam"
             OR intent."ProposedFutureRentCreditVnd"
                  IS DISTINCT FROM NEW."H4NextMonthRentCreditVnd"
             OR intent."ProposedManualRefundVnd"
                  IS DISTINCT FROM NEW."H4ManualRefundLiabilityVnd"
             OR (intent."ProposedUnpaidTopUpVnd">0 AND p."DepositTreatment"<>2)
             OR (intent."ProposedUnpaidTopUpVnd"=0 AND p."DepositTreatment"<>3)
             OR source."MonthlyRent" = target."MonthlyRent"
             OR target."DepositAmount" IS DISTINCT FROM target."MonthlyRent"
             OR carry."Amount" <= 0
             OR NEW."H4OriginalHeldDepositVnd" IS DISTINCT FROM
                NEW."CarriedDepositVnd"+NEW."H4NextMonthRentCreditVnd"
                  +NEW."H4ManualRefundLiabilityVnd"
             OR NEW."H4TargetRequiredDepositVnd" IS DISTINCT FROM
                NEW."CarriedDepositVnd"+NEW."H4UnpaidTopUpVnd"
             OR "BM_2HH4IntentMatchesSignedPolicy"(intent) IS DISTINCT FROM TRUE
           THEN
             RAISE EXCEPTION 'H4 completion violates signed money conservation'
               USING ERRCODE='23514';
           END IF;
           SELECT * INTO obligation
             FROM "BuildingManagementRoomChangeH4Obligations"
            WHERE "CompletionId"=NEW."Id";
           IF obligation."Id" IS NULL
             OR obligation."IntentId" IS DISTINCT FROM intent."Id"
             OR obligation."RequestId" IS DISTINCT FROM NEW."RequestId"
             OR obligation."SourceContractId" IS DISTINCT FROM source."Id"
             OR obligation."TargetContractId" IS DISTINCT FROM target."Id"
             OR obligation."RecordedByOwnerId" IS DISTINCT FROM NEW."CompletedByUserId"
             OR obligation."RecordedAtUtc" IS DISTINCT FROM NEW."CompletedAtUtc"
             OR obligation."Kind" IS DISTINCT FROM
               (CASE WHEN NEW."H4UnpaidTopUpVnd">0 THEN 1
                     WHEN NEW."H4NextMonthRentCreditVnd">0 THEN 2
                     ELSE 3 END)
             OR obligation."AmountVnd" IS DISTINCT FROM
               (CASE WHEN NEW."H4UnpaidTopUpVnd">0 THEN NEW."H4UnpaidTopUpVnd"
                     WHEN NEW."H4NextMonthRentCreditVnd">0
                       THEN NEW."H4NextMonthRentCreditVnd"
                     ELSE NEW."H4ManualRefundLiabilityVnd" END)
             OR obligation."DueDateVietnam" IS DISTINCT FROM NEW."H4TopUpDueDateVietnam"
             OR (SELECT count(*) FROM "BuildingManagementRoomChangeH4Obligations"
                  WHERE "CompletionId"=NEW."Id")<>1
           THEN
             RAISE EXCEPTION 'Missing exact H4 deferred receivable or exclusive liability'
               USING ERRCODE='23514';
           END IF;
         ELSIF EXISTS (
           SELECT 1 FROM "BuildingManagementRoomChangeH4Obligations"
            WHERE "CompletionId"=NEW."Id"
         ) THEN
           RAISE EXCEPTION 'Legacy same-deposit transfer cannot post H4 obligations'
             USING ERRCODE='23514';
         END IF;

         original_archive:=pricing."EvidenceJson"::jsonb;
         handover_json:=signed."EvidenceJson"::jsonb;
         IF original_archive->>'PricingRuleId' IS DISTINCT FROM day_rule."Id"::text
            OR original_archive->>'PricingRuleSha256'
               IS DISTINCT FROM day_rule."EvidenceSha256"
            OR original_archive->>'OwnerUserId'
               IS DISTINCT FROM rv."ReviewedByUserId"::text
            OR handover_json->>'HandoverId'
               IS DISTINCT FROM handover."Id"::text
            OR handover_json->>'StaffSignerId'
               IS DISTINCT FROM handover."AttestedByUserId"::text
            OR handover_json->>'OwnerSignerId'
                IS DISTINCT FROM original_archive->>'OwnerUserId'
            OR handover_json->>'TenantRepresentativeId' IS NULL
            OR handover_json->>'StaffSignerId' = handover_json->>'OwnerSignerId'
            OR handover_json->>'TenantRepresentativeId' =
                handover_json->>'StaffSignerId'
            OR handover_json->>'TenantRepresentativeId' =
                handover_json->>'OwnerSignerId'
            OR NOT EXISTS (
              SELECT 1 FROM "BuildingManagementContractTenants" ct
               WHERE ct."ContractId"=source."Id" AND ct."IsDeleted"=FALSE
                 AND ct."Role"=1
                 AND ct."TenantId"::text=handover_json->>'TenantRepresentativeId'
            )
         THEN
           RAISE EXCEPTION 'Pricing owner or physical handover three-party provenance invalid'
             USING ERRCODE='23514';
         END IF;
        
         IF EXISTS (
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=source."Id" AND t."IsDeleted"=FALSE
           EXCEPT
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=target."Id" AND t."IsDeleted"=FALSE
         ) OR EXISTS (
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=target."Id" AND t."IsDeleted"=FALSE
           EXCEPT
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=source."Id" AND t."IsDeleted"=FALSE
         ) OR (SELECT count(*) FROM "BuildingManagementContractTenants"
              WHERE "ContractId"=target."Id" AND "IsDeleted"=FALSE)
               <> roster."RosterCount"
         THEN
          RAISE EXCEPTION 'Mid-month transfer cannot split or alter tenant roster'
           USING ERRCODE='23514';
         END IF;
        
         IF invoice."Id" IS NULL OR invoice."ContractId" IS DISTINCT FROM source."Id"
           OR invoice."RoomId" IS DISTINCT FROM source."RoomId"
           OR invoice."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR invoice."BillingYear" <> extract(year from req."RequestedDate")::integer
           OR invoice."BillingMonth" <> extract(month from req."RequestedDate")::integer
           OR candidate."Id" IS NULL
           OR candidate."ContractId" IS DISTINCT FROM source."Id"
           OR candidate."RoomId" IS DISTINCT FROM source."RoomId"
           OR candidate."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR candidate."RentAmount"<=0
           OR receivable."Id" IS NULL OR receivable."Kind"<>1
           OR receivable."IssuedInvoiceId" IS DISTINCT FROM invoice."Id"
           OR receivable."ContractId" IS DISTINCT FROM source."Id"
           OR receivable."RoomId" IS DISTINCT FROM source."RoomId"
           OR receivable."OriginalAmount" IS DISTINCT FROM invoice."TotalAmount"
           OR receivable."SourceEvidenceSha256" IS DISTINCT FROM invoice."CandidateSha256"
         THEN
          RAISE EXCEPTION 'Mid-month source rent requires original monthly issued receivable'
            USING ERRCODE='23514';
         END IF;
        
         SELECT COALESCE(sum("SignedAmountVnd"),0) INTO correction_total
           FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
          WHERE "RequestId"=req."Id" AND "Kind"=1
            AND "OfficialReceivableId"=receivable."Id";
         SELECT COALESCE(sum(z."Amount"),0) INTO paid
           FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
          WHERE z."ReceivablePostingId"=receivable."Id" AND z."Accepted"=TRUE;
         corrected:=receivable."OriginalAmount"+correction_total;
         IF correction_total IS DISTINCT FROM NEW."SourceSignedAdjustmentVnd"
           OR correction_total IS DISTINCT FROM
             (day_rule."ProposedSourceRentVnd" - candidate."RentAmount")
           OR corrected IS DISTINCT FROM NEW."SourceAdjustedReceivableVnd"
           OR paid IS DISTINCT FROM NEW."SourceReviewedReceiptsVnd"
           OR corrected IS DISTINCT FROM paid
           OR corrected<=0
           OR EXISTS (SELECT 1
               FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
              WHERE z."ReceivablePostingId"=receivable."Id"
                AND z."Accepted"=FALSE)
           OR EXISTS (SELECT 1
               FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" x
              WHERE x."RequestId"=req."Id" AND x."Kind" IN (2,3))
           OR EXISTS (SELECT 1
               FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
              WHERE d."ContractId"=source."Id"
                AND (d."IssuedInvoiceId"<>invoice."Id" OR d."Accepted"<>TRUE
                  OR NOT EXISTS (
                   SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" j
                    WHERE j."Kind"=1 AND j."ApprovedAdjustmentId"=d."AdjustmentId"
                      AND j."RequestId"=req."Id")))
         THEN
          RAISE EXCEPTION 'D03 source rent, approvals, receivable payments or H4 cash split is unsettled'
            USING ERRCODE='23514';
         END IF;
         IF EXISTS (
            SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
             WHERE o."ContractId"=source."Id" AND o."Id"<>receivable."Id"
               AND (o."OriginalAmount" IS DISTINCT FROM
                   (SELECT COALESCE(sum(z."Amount"),0)
                      FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                     WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=TRUE)
                 OR EXISTS (SELECT 1 FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                             WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=FALSE))
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
            WHERE i."ContractId"=source."Id" AND NOT EXISTS (
              SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
               WHERE o."IssuedInvoiceId"=i."Id" AND o."Kind"=1)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
            WHERE i."ContractId"=source."Id" AND NOT EXISTS (
              SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
               WHERE o."IssuedInvoiceId"=i."Id" AND o."Kind"=2)
         ) THEN
          RAISE EXCEPTION 'Mid-month source has other unposted or unpaid official debt'
           USING ERRCODE='23514';
         END IF;
        
         -- The original source deposit must still have one exclusive, completed
         -- source→target carry; a refund or second transfer of the same funds fails.
         SELECT COALESCE(sum(s."Amount"),0) INTO original_held
           FROM "BuildingManagementDepositSettlements" s
          WHERE s."TargetContractId"=source."Id" AND s."Type"=3 AND s."Status"=2;
         IF EXISTS (
           SELECT 1 FROM "BuildingManagementDepositSettlements" s
           LEFT JOIN "BuildingManagementPayments" pay ON pay."Id"=s."PaymentId"
           WHERE s."TargetContractId"=source."Id" AND s."Type"=3
             AND (s."Status"<>2 OR s."PaymentId" IS NULL
               OR pay."Id" IS NULL OR pay."IsDeleted"=TRUE
               OR pay."Status"<>2 OR pay."PaidAt" IS NULL
               OR pay."Amount"<>s."Amount"
               OR pay."DepositDisposition" NOT IN (1,2)
               OR NOT (
                 (pay."Purpose"=2 AND pay."ContractId"=source."Id"
                   AND s."SourceRoomReservationId" IS NULL)
                 OR (pay."Purpose"=1 AND pay."RoomReservationId" IS NOT NULL
                   AND pay."RoomReservationId"=s."SourceRoomReservationId")
               )
               OR (SELECT count(*) FROM "BuildingManagementDepositSettlements" z
                    WHERE z."PaymentId"=pay."Id")<>1)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementPayments" pay
            WHERE pay."ContractId"=source."Id" AND pay."Purpose"=2
              AND pay."Status"=2 AND NOT EXISTS (
                SELECT 1 FROM "BuildingManagementDepositSettlements" s
                 WHERE s."PaymentId"=pay."Id"
                   AND s."TargetContractId"=source."Id"
                   AND s."Type"=3 AND s."Status"=2)
         ) THEN
          RAISE EXCEPTION 'Mid-month carry source payments not fully proven'
           USING ERRCODE='23514';
         END IF;
         IF original_held IS DISTINCT FROM source."DepositAmount"
            OR EXISTS (
              SELECT 1 FROM "BuildingManagementDepositSettlements" s
               WHERE s."SourceContractId"=source."Id"
                 AND s."Id"<>carry."Id"
            )
            OR EXISTS (
              SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" c
               WHERE c."Id"<>NEW."Id" AND
                  (c."SourceContractId"=NEW."SourceContractId"
                   OR c."TargetContractId"=NEW."TargetContractId")
            )
         THEN
           RAISE EXCEPTION 'Cash-backed held source deposit was previously disposed or double-spent'
             USING ERRCODE='23514';
         END IF;
        
         IF EXISTS (
           SELECT 1 FROM "BuildingManagementRoomReservations" z
            WHERE z."RoomId"=target."RoomId"
              AND z."IsDeleted"=FALSE AND z."Status" IN (1,2)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementContracts" z
            WHERE z."RoomId"=target."RoomId"
              AND z."Id"<>target."Id" AND z."IsDeleted"=FALSE
              AND z."Status" IN (2,5)
         ) THEN
          RAISE EXCEPTION 'Target room has competing contract/reservation'
           USING ERRCODE='23514';
         END IF;
         IF EXISTS (
            SELECT 1 FROM "BuildingManagementMonthlyInvoiceCandidates" m
             WHERE m."ContractId"=target."Id"
               AND m."BillingYear"=extract(year from req."RequestedDate")::integer
               AND m."BillingMonth"=extract(month from req."RequestedDate")::integer
               AND m."RentAmount" IS DISTINCT FROM NEW."TargetFirstMonthRentVnd"
         ) THEN
           RAISE EXCEPTION 'Target first-month invoice must use frozen prorated rent'
             USING ERRCODE='23514';
         END IF;
        
         IF NEW."EvidenceSha256" IS DISTINCT FROM "BM_Calculate2HMidCompletionSha"(NEW) THEN
          RAISE EXCEPTION 'Mid-month completion canonical fingerprint mismatch'
            USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $mid$;
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeMidMonthCompletions"
          IN ACCESS EXCLUSIVE MODE;
        LOCK TABLE "BuildingManagementRoomChangeH4Obligations"
          IN ACCESS EXCLUSIVE MODE;
        DO $retain$
        BEGIN
          IF EXISTS(
            SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
             WHERE "H4DispositionIntentId" IS NOT NULL
          ) OR EXISTS(
            SELECT 1 FROM "BuildingManagementRoomChangeH4Obligations"
          ) THEN
            RAISE EXCEPTION 'Cannot rollback completed H4 obligations'
              USING ERRCODE='23514';
          END IF;
        END;
        $retain$;
        DROP TABLE "BuildingManagementRoomChangeH4Obligations";
        DROP FUNCTION "BM_2HGuardH4Obligation"();
        DROP FUNCTION "BM_2HNoTruncateH4Obligation"();

        CREATE OR REPLACE FUNCTION "BM_Verify2HMidMonthCommit"()
        RETURNS trigger LANGUAGE plpgsql AS $mid$
        DECLARE req record; p record; rv record; roster record; handover record;
                source record; target record; carry record; day_rule record;
                pricing record; signed record; invoice record; candidate record;
                receivable record; original_net numeric; corrected numeric;
                paid numeric; due_other bigint; correction_total numeric;
                target_status integer; source_status integer; original_held numeric;
                bank_claims bigint; original_archive jsonb; handover_json jsonb;
                billing_period date;
        BEGIN
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION 'Mid-month commit must be SERIALIZABLE' USING ERRCODE='23514';
         END IF;
        
         SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId";
         SELECT * INTO p FROM "BuildingManagementRoomChangeBusinessPolicies"
          WHERE "Id"=NEW."PolicyId";
         SELECT * INTO rv FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
          WHERE "Id"=NEW."PolicyReviewId";
         SELECT * INTO roster FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
          WHERE "Id"=NEW."RosterSnapshotId";
         SELECT * INTO handover FROM "BuildingManagementRoomChangeHandoverEvidence"
          WHERE "Id"=NEW."HandoverEvidenceId";
         SELECT * INTO day_rule FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
          WHERE "Id"=NEW."PricingRuleId";
         SELECT * INTO pricing FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."PricingArchiveId";
         SELECT * INTO signed FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."ThreePartyArchiveId";
         SELECT * INTO source FROM "BuildingManagementContracts"
          WHERE "Id"=NEW."SourceContractId";
         SELECT * INTO target FROM "BuildingManagementContracts"
          WHERE "Id"=NEW."TargetContractId";
         SELECT * INTO carry FROM "BuildingManagementDepositSettlements"
          WHERE "Id"=NEW."CarryForwardSettlementId";
         SELECT * INTO invoice FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
          WHERE "Id"=NEW."SourceMonthlyInvoiceId";
         SELECT * INTO receivable FROM "BuildingManagementOfficialInvoiceReceivables"
          WHERE "Id"=NEW."SourceReceivableId";
         SELECT * INTO candidate FROM "BuildingManagementMonthlyInvoiceCandidates"
          WHERE "Id"=invoice."CandidateId";
         SELECT "Status" INTO source_status FROM "BuildingManagementRooms"
          WHERE "Id"=NEW."SourceRoomId";
         SELECT "Status" INTO target_status FROM "BuildingManagementRooms"
          WHERE "Id"=NEW."TargetRoomId";
        
         IF req."Id" IS NULL THEN
          RAISE EXCEPTION 'Missing transfer request' USING ERRCODE='23514';
         END IF;
         billing_period:=make_date(extract(year from req."RequestedDate")::integer,
                                   extract(month from req."RequestedDate")::integer,1);
        
         IF req."Status"<>4
           OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
           OR req."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
           OR req."TargetRoomId" IS DISTINCT FROM NEW."TargetRoomId"
           OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
           OR req."ReviewedByUserId" IS DISTINCT FROM NEW."CompletedByUserId"
           OR req."ReviewedAtUtc" IS DISTINCT FROM NEW."CompletedAtUtc"
           OR req."ReviewNotes" IS DISTINCT FROM NEW."CompletionReference"
           OR req."RequestedDate" =
              (date_trunc('month',req."RequestedDate"::timestamp)
                  + interval '1 month - 1 day')::date
           OR NEW."CompletedAtUtc" > clock_timestamp()
           OR (NEW."CompletedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                < req."RequestedDate"
           OR p."Id" IS NULL OR p."RequestId" IS DISTINCT FROM req."Id"
           OR p."SourceContractId" IS DISTINCT FROM source."Id"
           OR p."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR p."ContractTreatment"<>1 OR p."PricingTreatment"<>2
           OR p."DepositTreatment"<>1 OR p."TenantTreatment"<>1
           OR rv."Id" IS NULL OR rv."Accepted" IS DISTINCT FROM TRUE
           OR rv."PolicyId" IS DISTINCT FROM p."Id"
           OR rv."RequestId" IS DISTINCT FROM req."Id"
           OR rv."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR rv."ProposalSha256" IS DISTINCT FROM p."EvidenceSha256"
           OR rv."ReviewedByUserId"=p."ProposedByUserId"
           OR roster."Id" IS NULL OR p."RosterSnapshotId" IS DISTINCT FROM roster."Id"
           OR p."RosterSha256" IS DISTINCT FROM roster."RosterSha256"
           OR roster."RoomChangeRequestId" IS DISTINCT FROM req."Id"
           OR handover."Id" IS NULL OR handover."RequestId" IS DISTINCT FROM req."Id"
           OR handover."SourceContractId" IS DISTINCT FROM source."Id"
           OR handover."SourceRoomId" IS DISTINCT FROM req."SourceRoomId"
           OR handover."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR handover."HandoverDate" IS DISTINCT FROM req."RequestedDate"
           OR day_rule."Id" IS NULL OR day_rule."RequestId" IS DISTINCT FROM req."Id"
           OR day_rule."SourceContractId" IS DISTINCT FROM source."Id"
           OR day_rule."PolicyId" IS DISTINCT FROM p."Id"
           OR day_rule."PolicyReviewId" IS DISTINCT FROM rv."Id"
           OR day_rule."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR day_rule."TransferDate" IS DISTINCT FROM req."RequestedDate"
           OR day_rule."SourceChargeableDays"<=0
           OR day_rule."TargetChargeableDays"<=0
           OR day_rule."CalendarDays" IS DISTINCT FROM
              day_rule."SourceChargeableDays"+day_rule."TargetChargeableDays"
           OR pricing."Id" IS NULL OR pricing."Kind"<>1
           OR pricing."RequestId" IS DISTINCT FROM req."Id"
           OR pricing."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR pricing."ParentEvidenceId" IS DISTINCT FROM day_rule."Id"
           OR pricing."ParentEvidenceSha256" IS DISTINCT FROM day_rule."EvidenceSha256"
           OR signed."Id" IS NULL OR signed."Kind"<>4
           OR signed."RequestId" IS DISTINCT FROM req."Id"
           OR signed."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR signed."ParentEvidenceId" IS DISTINCT FROM handover."Id"
           OR signed."ParentEvidenceSha256" IS DISTINCT FROM handover."EvidenceSha256"
           OR source."Id" IS NULL OR source."Status"<>3 OR source."EndReason"<>4
           OR source."ActualEndedAt" IS NULL
           OR source."RoomId" IS DISTINCT FROM req."SourceRoomId"
           OR target."Id" IS NULL OR target."Status"<>2
           OR target."RoomId" IS DISTINCT FROM req."TargetRoomId"
           OR target."RenewedFromContractId" IS DISTINCT FROM source."Id"
           OR target."StartDate"::date IS DISTINCT FROM
               (billing_period + day_rule."SourceChargeableDays")
           OR source."MonthlyRent" IS DISTINCT FROM target."MonthlyRent"
           OR source."MonthlyRent" IS DISTINCT FROM day_rule."SourceMonthlyRentVnd"
           OR target."MonthlyRent" IS DISTINCT FROM day_rule."TargetMonthlyRentVnd"
           OR source."DepositAmount"<=0
           OR target."DepositAmount" IS DISTINCT FROM source."DepositAmount"
           OR target_status<>2 OR source_status IS DISTINCT FROM NEW."SourceRoomDisposition"
           OR NEW."TargetFirstMonthRentVnd" IS DISTINCT FROM day_rule."ProposedTargetRentVnd"
           OR carry."Id" IS NULL OR carry."Type"<>5 OR carry."Status"<>2
           OR carry."SourceContractId" IS DISTINCT FROM source."Id"
           OR carry."TargetContractId" IS DISTINCT FROM target."Id"
           OR carry."Amount" IS DISTINCT FROM source."DepositAmount"
           OR NEW."CarriedDepositVnd" IS DISTINCT FROM carry."Amount"
           OR carry."CompletedAt" IS NULL
           OR EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeCompletions"
              WHERE "SourceContractId"=NEW."SourceContractId" OR "RequestId"=NEW."RequestId")
         THEN
          RAISE EXCEPTION 'Mid-month atomic completion contradicts approved H1–H6 scope'
           USING ERRCODE='23514';
         END IF;
        
         original_archive:=pricing."EvidenceJson"::jsonb;
         handover_json:=signed."EvidenceJson"::jsonb;
         IF original_archive->>'PricingRuleId' IS DISTINCT FROM day_rule."Id"::text
            OR original_archive->>'PricingRuleSha256'
               IS DISTINCT FROM day_rule."EvidenceSha256"
            OR original_archive->>'OwnerUserId'
               IS DISTINCT FROM rv."ReviewedByUserId"::text
            OR handover_json->>'HandoverId'
               IS DISTINCT FROM handover."Id"::text
            OR handover_json->>'StaffSignerId'
               IS DISTINCT FROM handover."AttestedByUserId"::text
            OR handover_json->>'OwnerSignerId'
                IS DISTINCT FROM original_archive->>'OwnerUserId'
            OR handover_json->>'TenantRepresentativeId' IS NULL
            OR handover_json->>'StaffSignerId' = handover_json->>'OwnerSignerId'
            OR handover_json->>'TenantRepresentativeId' =
                handover_json->>'StaffSignerId'
            OR handover_json->>'TenantRepresentativeId' =
                handover_json->>'OwnerSignerId'
            OR NOT EXISTS (
              SELECT 1 FROM "BuildingManagementContractTenants" ct
               WHERE ct."ContractId"=source."Id" AND ct."IsDeleted"=FALSE
                 AND ct."Role"=1
                 AND ct."TenantId"::text=handover_json->>'TenantRepresentativeId'
            )
         THEN
           RAISE EXCEPTION 'Pricing owner or physical handover three-party provenance invalid'
             USING ERRCODE='23514';
         END IF;
        
         IF EXISTS (
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=source."Id" AND t."IsDeleted"=FALSE
           EXCEPT
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=target."Id" AND t."IsDeleted"=FALSE
         ) OR EXISTS (
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=target."Id" AND t."IsDeleted"=FALSE
           EXCEPT
           SELECT t."TenantId",t."Role"
             FROM "BuildingManagementContractTenants" t
            WHERE t."ContractId"=source."Id" AND t."IsDeleted"=FALSE
         ) OR (SELECT count(*) FROM "BuildingManagementContractTenants"
              WHERE "ContractId"=target."Id" AND "IsDeleted"=FALSE)
               <> roster."RosterCount"
         THEN
          RAISE EXCEPTION 'Mid-month transfer cannot split or alter tenant roster'
           USING ERRCODE='23514';
         END IF;
        
         IF invoice."Id" IS NULL OR invoice."ContractId" IS DISTINCT FROM source."Id"
           OR invoice."RoomId" IS DISTINCT FROM source."RoomId"
           OR invoice."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR invoice."BillingYear" <> extract(year from req."RequestedDate")::integer
           OR invoice."BillingMonth" <> extract(month from req."RequestedDate")::integer
           OR candidate."Id" IS NULL
           OR candidate."ContractId" IS DISTINCT FROM source."Id"
           OR candidate."RoomId" IS DISTINCT FROM source."RoomId"
           OR candidate."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR candidate."RentAmount"<=0
           OR receivable."Id" IS NULL OR receivable."Kind"<>1
           OR receivable."IssuedInvoiceId" IS DISTINCT FROM invoice."Id"
           OR receivable."ContractId" IS DISTINCT FROM source."Id"
           OR receivable."RoomId" IS DISTINCT FROM source."RoomId"
           OR receivable."OriginalAmount" IS DISTINCT FROM invoice."TotalAmount"
           OR receivable."SourceEvidenceSha256" IS DISTINCT FROM invoice."CandidateSha256"
         THEN
          RAISE EXCEPTION 'Mid-month source rent requires original monthly issued receivable'
            USING ERRCODE='23514';
         END IF;
        
         SELECT COALESCE(sum("SignedAmountVnd"),0) INTO correction_total
           FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
          WHERE "RequestId"=req."Id" AND "Kind"=1
            AND "OfficialReceivableId"=receivable."Id";
         SELECT COALESCE(sum(z."Amount"),0) INTO paid
           FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
          WHERE z."ReceivablePostingId"=receivable."Id" AND z."Accepted"=TRUE;
         corrected:=receivable."OriginalAmount"+correction_total;
         IF correction_total IS DISTINCT FROM NEW."SourceSignedAdjustmentVnd"
           OR correction_total IS DISTINCT FROM
             (day_rule."ProposedSourceRentVnd" - candidate."RentAmount")
           OR corrected IS DISTINCT FROM NEW."SourceAdjustedReceivableVnd"
           OR paid IS DISTINCT FROM NEW."SourceReviewedReceiptsVnd"
           OR corrected IS DISTINCT FROM paid
           OR corrected<=0
           OR EXISTS (SELECT 1
               FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
              WHERE z."ReceivablePostingId"=receivable."Id"
                AND z."Accepted"=FALSE)
           OR EXISTS (SELECT 1
               FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" x
              WHERE x."RequestId"=req."Id" AND x."Kind" IN (2,3))
           OR EXISTS (SELECT 1
               FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
              WHERE d."ContractId"=source."Id"
                AND (d."IssuedInvoiceId"<>invoice."Id" OR d."Accepted"<>TRUE
                  OR NOT EXISTS (
                   SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" j
                    WHERE j."Kind"=1 AND j."ApprovedAdjustmentId"=d."AdjustmentId"
                      AND j."RequestId"=req."Id")))
         THEN
          RAISE EXCEPTION 'D03 source rent, approvals, receivable payments or H4 cash split is unsettled'
            USING ERRCODE='23514';
         END IF;
         IF EXISTS (
            SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
             WHERE o."ContractId"=source."Id" AND o."Id"<>receivable."Id"
               AND (o."OriginalAmount" IS DISTINCT FROM
                   (SELECT COALESCE(sum(z."Amount"),0)
                      FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                     WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=TRUE)
                 OR EXISTS (SELECT 1 FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                             WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=FALSE))
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
            WHERE i."ContractId"=source."Id" AND NOT EXISTS (
              SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
               WHERE o."IssuedInvoiceId"=i."Id" AND o."Kind"=1)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
            WHERE i."ContractId"=source."Id" AND NOT EXISTS (
              SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
               WHERE o."IssuedInvoiceId"=i."Id" AND o."Kind"=2)
         ) THEN
          RAISE EXCEPTION 'Mid-month source has other unposted or unpaid official debt'
           USING ERRCODE='23514';
         END IF;
        
         -- The original source deposit must still have one exclusive, completed
         -- source→target carry; a refund or second transfer of the same funds fails.
         SELECT COALESCE(sum(s."Amount"),0) INTO original_held
           FROM "BuildingManagementDepositSettlements" s
          WHERE s."TargetContractId"=source."Id" AND s."Type"=3 AND s."Status"=2;
         IF EXISTS (
           SELECT 1 FROM "BuildingManagementDepositSettlements" s
           LEFT JOIN "BuildingManagementPayments" pay ON pay."Id"=s."PaymentId"
           WHERE s."TargetContractId"=source."Id" AND s."Type"=3
             AND (s."Status"<>2 OR s."PaymentId" IS NULL
               OR pay."Id" IS NULL OR pay."IsDeleted"=TRUE
               OR pay."Status"<>2 OR pay."PaidAt" IS NULL
               OR pay."Amount"<>s."Amount"
               OR pay."DepositDisposition" NOT IN (1,2)
               OR NOT (
                 (pay."Purpose"=2 AND pay."ContractId"=source."Id"
                   AND s."SourceRoomReservationId" IS NULL)
                 OR (pay."Purpose"=1 AND pay."RoomReservationId" IS NOT NULL
                   AND pay."RoomReservationId"=s."SourceRoomReservationId")
               )
               OR (SELECT count(*) FROM "BuildingManagementDepositSettlements" z
                    WHERE z."PaymentId"=pay."Id")<>1)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementPayments" pay
            WHERE pay."ContractId"=source."Id" AND pay."Purpose"=2
              AND pay."Status"=2 AND NOT EXISTS (
                SELECT 1 FROM "BuildingManagementDepositSettlements" s
                 WHERE s."PaymentId"=pay."Id"
                   AND s."TargetContractId"=source."Id"
                   AND s."Type"=3 AND s."Status"=2)
         ) THEN
          RAISE EXCEPTION 'Mid-month carry source payments not fully proven'
           USING ERRCODE='23514';
         END IF;
         IF original_held IS DISTINCT FROM source."DepositAmount"
            OR EXISTS (
              SELECT 1 FROM "BuildingManagementDepositSettlements" s
               WHERE s."SourceContractId"=source."Id"
                 AND s."Id"<>carry."Id"
            )
            OR EXISTS (
              SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" c
               WHERE c."Id"<>NEW."Id" AND
                  (c."SourceContractId"=NEW."SourceContractId"
                   OR c."TargetContractId"=NEW."TargetContractId")
            )
         THEN
           RAISE EXCEPTION 'Cash-backed held source deposit was previously disposed or double-spent'
             USING ERRCODE='23514';
         END IF;
        
         IF EXISTS (
           SELECT 1 FROM "BuildingManagementRoomReservations" z
            WHERE z."RoomId"=target."RoomId"
              AND z."IsDeleted"=FALSE AND z."Status" IN (1,2)
         ) OR EXISTS (
           SELECT 1 FROM "BuildingManagementContracts" z
            WHERE z."RoomId"=target."RoomId"
              AND z."Id"<>target."Id" AND z."IsDeleted"=FALSE
              AND z."Status" IN (2,5)
         ) THEN
          RAISE EXCEPTION 'Target room has competing contract/reservation'
           USING ERRCODE='23514';
         END IF;
         IF EXISTS (
            SELECT 1 FROM "BuildingManagementMonthlyInvoiceCandidates" m
             WHERE m."ContractId"=target."Id"
               AND m."BillingYear"=extract(year from req."RequestedDate")::integer
               AND m."BillingMonth"=extract(month from req."RequestedDate")::integer
               AND m."RentAmount" IS DISTINCT FROM NEW."TargetFirstMonthRentVnd"
         ) THEN
           RAISE EXCEPTION 'Target first-month invoice must use frozen prorated rent'
             USING ERRCODE='23514';
         END IF;
        
         IF NEW."EvidenceSha256" IS DISTINCT FROM encode(
             sha256(convert_to(concat_ws(E'\n',
               NEW."Id"::text,NEW."RequestId"::text,
               NEW."SourceContractId"::text,NEW."TargetContractId"::text,
               NEW."SourceRoomId"::text,NEW."TargetRoomId"::text,
               NEW."BuildingId"::text,NEW."PolicyId"::text,
               NEW."PolicyReviewId"::text,NEW."RosterSnapshotId"::text,
               NEW."HandoverEvidenceId"::text,NEW."PricingArchiveId"::text,
               NEW."ThreePartyArchiveId"::text,NEW."PricingRuleId"::text,
               NEW."SourceMonthlyInvoiceId"::text,NEW."SourceReceivableId"::text,
               NEW."SourceSignedAdjustmentVnd"::text,
               NEW."SourceAdjustedReceivableVnd"::text,
               NEW."SourceReviewedReceiptsVnd"::text,
               NEW."TargetFirstMonthRentVnd"::text,
               NEW."CarryForwardSettlementId"::text,
               NEW."CarriedDepositVnd"::text,
               NEW."SourceRoomDisposition"::text,
               NEW."DispositionEvidenceReference",
               NEW."CompletionReference",
               NEW."CompletedByUserId"::text,
               to_char(NEW."CompletedAtUtc" AT TIME ZONE 'UTC',
                       'YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z'
             ),'UTF8')),'hex') THEN
          RAISE EXCEPTION 'Mid-month completion canonical fingerprint mismatch'
            USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $mid$;

        DROP FUNCTION "BM_Calculate2HMidCompletionSha"(
          "BuildingManagementRoomChangeMidMonthCompletions");
        ALTER TABLE "BuildingManagementRoomChangeMidMonthCompletions"
          DROP CONSTRAINT "CK_BM_2H_H4MoneyShape",
          DROP COLUMN "H4DispositionIntentId",
          DROP COLUMN "H4IntentSha256",
          DROP COLUMN "H4OriginalHeldDepositVnd",
          DROP COLUMN "H4TargetRequiredDepositVnd",
          DROP COLUMN "H4UnpaidTopUpVnd",
          DROP COLUMN "H4TopUpDueDateVietnam",
          DROP COLUMN "H4NextMonthRentCreditVnd",
          DROP COLUMN "H4ManualRefundLiabilityVnd";
        """);
}
