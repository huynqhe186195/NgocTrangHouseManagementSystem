using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Separate H1/H2/H3/H5/H6 same-deposit midmonth atomic completion gate.
/// The full old month-end path remains intact. H4 top-up/refund cash
/// cannot execute on this restricted lane.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104100000_Phase2HMidMonthAtomicCompletion")]
public sealed class Phase2HMidMonthAtomicCompletion : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        
        CREATE TABLE "BuildingManagementRoomChangeMidMonthCompletions" (
         "Id" uuid PRIMARY KEY,
         "RequestId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
         "SourceContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
         "TargetContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
         "SourceRoomId" uuid NOT NULL REFERENCES "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
         "TargetRoomId" uuid NOT NULL REFERENCES "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
         "BuildingId" uuid NOT NULL,
         "PolicyId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeBusinessPolicies"("Id") ON DELETE RESTRICT,
         "PolicyReviewId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeBusinessPolicyReviews"("Id") ON DELETE RESTRICT,
         "RosterSnapshotId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeTenantRosterSnapshots"("Id") ON DELETE RESTRICT,
         "HandoverEvidenceId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeHandoverEvidence"("Id") ON DELETE RESTRICT,
         "PricingArchiveId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeEvidenceArchives"("Id") ON DELETE RESTRICT,
         "ThreePartyArchiveId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeEvidenceArchives"("Id") ON DELETE RESTRICT,
         "PricingRuleId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeDayPricingRuleCandidates"("Id") ON DELETE RESTRICT,
         "SourceMonthlyInvoiceId" uuid NOT NULL REFERENCES "BuildingManagementMonthlyIssuedInvoiceLedger"("Id") ON DELETE RESTRICT,
         "SourceReceivableId" uuid NOT NULL REFERENCES "BuildingManagementOfficialInvoiceReceivables"("Id") ON DELETE RESTRICT,
         "SourceSignedAdjustmentVnd" numeric(18,0) NOT NULL,
         "SourceAdjustedReceivableVnd" numeric(18,0) NOT NULL,
         "SourceReviewedReceiptsVnd" numeric(18,0) NOT NULL,
         "TargetFirstMonthRentVnd" numeric(18,0) NOT NULL,
         "CarryForwardSettlementId" uuid NOT NULL REFERENCES "BuildingManagementDepositSettlements"("Id") ON DELETE RESTRICT,
         "CarriedDepositVnd" numeric(18,0) NOT NULL,
         "SourceRoomDisposition" integer NOT NULL,
         "DispositionEvidenceReference" varchar(500) NOT NULL,
         "CompletionReference" varchar(500) NOT NULL,
         "CompletedByUserId" uuid NOT NULL,
         "CompletedAtUtc" timestamptz NOT NULL,
         "EvidenceSha256" varchar(64) NOT NULL,
         CONSTRAINT "CK_BM_2HMidComplete_Valid" CHECK (
          "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
          AND "SourceContractId"<>"TargetContractId"
          AND "SourceRoomId"<>"TargetRoomId"
          AND "SourceAdjustedReceivableVnd">0
          AND "SourceAdjustedReceivableVnd"="SourceReviewedReceiptsVnd"
          AND "TargetFirstMonthRentVnd">0
          AND "CarriedDepositVnd">0
          AND "SourceRoomDisposition" IN (1,3,4)
          AND length(btrim("DispositionEvidenceReference")) BETWEEN 12 AND 500
          AND length(btrim("CompletionReference")) BETWEEN 12 AND 500
          AND "CompletedByUserId"<>'00000000-0000-0000-0000-000000000000'::uuid
          AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
         )
        );
        CREATE UNIQUE INDEX "UX_BM_2HMidComplete_Request"
         ON "BuildingManagementRoomChangeMidMonthCompletions"("RequestId");
        CREATE UNIQUE INDEX "UX_BM_2HMidComplete_Source"
         ON "BuildingManagementRoomChangeMidMonthCompletions"("SourceContractId");
        CREATE UNIQUE INDEX "UX_BM_2HMidComplete_Target"
         ON "BuildingManagementRoomChangeMidMonthCompletions"("TargetContractId");
        CREATE UNIQUE INDEX "UX_BM_2HMidComplete_Carry"
         ON "BuildingManagementRoomChangeMidMonthCompletions"("CarryForwardSettlementId");
        
        CREATE FUNCTION "BM_Freeze2HMidMonthCompletion"()
        RETURNS trigger LANGUAGE plpgsql AS $immutable$
        BEGIN
         IF TG_OP <> 'INSERT' THEN
          RAISE EXCEPTION '2H mid-month completion is immutable' USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION '2H mid-month completion requires SERIALIZABLE' USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $immutable$;
        CREATE TRIGGER "TR_BM_2HMidComplete_Immutable"
         BEFORE INSERT OR UPDATE OR DELETE
         ON "BuildingManagementRoomChangeMidMonthCompletions"
         FOR EACH ROW EXECUTE FUNCTION "BM_Freeze2HMidMonthCompletion"();
        CREATE FUNCTION "BM_NoTruncate2HMidComplete"()
        RETURNS trigger LANGUAGE plpgsql AS $no$
        BEGIN
         RAISE EXCEPTION '2H mid-month completion cannot be truncated' USING ERRCODE='23514';
        END;
        $no$;
        CREATE TRIGGER "TR_BM_2HMidComplete_NoTruncate"
         BEFORE TRUNCATE ON "BuildingManagementRoomChangeMidMonthCompletions"
         FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncate2HMidComplete"();
        
        CREATE FUNCTION "BM_Verify2HMidMonthCommit"()
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
           -- D01/D02 Owner-confirmed for NEW execution on 2026-11-04:
           -- target owns handover VN civil date; nearest whole VND wins.
           -- Legacy signed source-day/truncation documents remain historical
           -- evidence but may never complete a newly executed transfer.
           OR day_rule."TransferDayOwner" IS DISTINCT FROM 2
           OR day_rule."Rounding" IS DISTINCT FROM 1
           OR day_rule."SourceChargeableDays" IS DISTINCT FROM
              extract(day from req."RequestedDate")::integer-1
           OR day_rule."CalendarDays" IS DISTINCT FROM
              extract(day from (date_trunc('month',req."RequestedDate"::timestamp)
                + interval '1 month - 1 day'))::integer
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
        
        CREATE CONSTRAINT TRIGGER "TR_BM_2HMidComplete_CommitGate"
         AFTER INSERT ON "BuildingManagementRoomChangeMidMonthCompletions"
         DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION "BM_Verify2HMidMonthCommit"();
        
        -- Keep old end/terminal contract gates exact, allowing only a NEW
        -- independently deferred and unique 2H mid-month completion in addition.
        CREATE OR REPLACE FUNCTION "BM_RequireAtomicRoomChangeAtContractEnd"()
        RETURNS trigger LANGUAGE plpgsql AS $end$
        BEGIN
         IF OLD."Status"=2 AND NEW."Status"=3 AND NEW."EndReason"=4
           AND NOT EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeCompletions" c
             JOIN "BuildingManagementRoomChangeRequests" r ON r."Id"=c."RequestId"
            WHERE c."SourceContractId"=NEW."Id" AND c."SourceRoomId"=OLD."RoomId"
              AND c."SourceContractId"=r."SourceContractId"
              AND c."SourceRoomId"=r."SourceRoomId"
              AND c."TargetRoomId"=r."TargetRoomId"
              AND c."TargetContractId"<>NEW."Id" AND r."Status"=4
              AND r."ReviewedByUserId"=c."CompletedByUserId"
              AND r."ReviewedAtUtc"=c."CompletedAtUtc"
           )
           AND NOT EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" c
             JOIN "BuildingManagementRoomChangeRequests" r ON r."Id"=c."RequestId"
            WHERE c."SourceContractId"=NEW."Id" AND c."SourceRoomId"=OLD."RoomId"
              AND c."SourceContractId"=r."SourceContractId"
              AND c."SourceRoomId"=r."SourceRoomId"
              AND c."TargetRoomId"=r."TargetRoomId"
              AND c."TargetContractId"<>NEW."Id" AND r."Status"=4
              AND r."ReviewedByUserId"=c."CompletedByUserId"
              AND r."ReviewedAtUtc"=c."CompletedAtUtc"
           ) THEN
           RAISE EXCEPTION 'RoomChanged requires a signed same-transaction completion'
            USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $end$;
        CREATE OR REPLACE FUNCTION "BM_VerifyRoomChangeTerminalRequest"()
        RETURNS trigger LANGUAGE plpgsql AS $terminal$
        BEGIN
         IF NEW."Status"=4
           AND NOT EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeCompletions" c
             WHERE c."RequestId"=NEW."Id"
           )
           AND NOT EXISTS (
            SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" c
             WHERE c."RequestId"=NEW."Id"
           ) THEN
          RAISE EXCEPTION 'Room change completed request requires atomic ledger'
            USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $terminal$;
        
        -- Existing exact source-room reservation/booking collision check can also
        -- validate this new completion record because its scope columns are equal.
        CREATE CONSTRAINT TRIGGER "TR_BM_2HMidComplete_SourceDisposition"
         AFTER INSERT ON "BuildingManagementRoomChangeMidMonthCompletions"
         DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRoomChangeSourceDispositionAtCommit"();
        
        -- Block a second completion across the old/new transfer ledgers.
        CREATE FUNCTION "BM_Guard2HMidOldLaneDoubleCompletion"()
        RETURNS trigger LANGUAGE plpgsql AS $one$
        BEGIN
         IF EXISTS (
          SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" c
           WHERE c."RequestId"=NEW."RequestId"
              OR c."SourceContractId"=NEW."SourceContractId"
              OR c."TargetContractId"=NEW."TargetContractId"
         ) THEN
           RAISE EXCEPTION 'Source/target contract already used in mid-month transfer'
            USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $one$;
        CREATE TRIGGER "TR_BM_2HMidComplete_MutualExclusion"
         BEFORE INSERT ON "BuildingManagementRoomChangeCompletions"
         FOR EACH ROW EXECUTE FUNCTION "BM_Guard2HMidOldLaneDoubleCompletion"();
        
        -- Ensure that later invoice issue cannot re-charge a whole target month.
        CREATE FUNCTION "BM_Require2HMidTargetFirstMonthProration"()
        RETURNS trigger LANGUAGE plpgsql AS $rent$
        DECLARE c record; requested date;
        BEGIN
         SELECT m.*,r."RequestedDate" INTO c
           FROM "BuildingManagementRoomChangeMidMonthCompletions" m
           JOIN "BuildingManagementRoomChangeRequests" r ON r."Id"=m."RequestId"
          WHERE m."TargetContractId"=NEW."ContractId"
            AND extract(year from r."RequestedDate")::integer=NEW."BillingYear"
            AND extract(month from r."RequestedDate")::integer=NEW."BillingMonth";
         IF c."Id" IS NOT NULL
            AND NEW."RentAmount" IS DISTINCT FROM c."TargetFirstMonthRentVnd" THEN
           RAISE EXCEPTION 'First transferred target month may not be billed at full rent'
            USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $rent$;
        CREATE TRIGGER "TR_BM_2HMid_TargetInvoiceProration"
         BEFORE INSERT OR UPDATE ON "BuildingManagementMonthlyInvoiceCandidates"
         FOR EACH ROW EXECUTE FUNCTION "BM_Require2HMidTargetFirstMonthProration"();
        
        CREATE FUNCTION "BM_Freeze2HMidCompletedSource"()
        RETURNS trigger LANGUAGE plpgsql AS $freeze$
        BEGIN
         IF TG_OP='DELETE' THEN
           IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                        WHERE "SourceContractId"=OLD."Id") THEN
             RAISE EXCEPTION 'Completed mid-month transfer source cannot be deleted'
               USING ERRCODE='23514';
           END IF;
           RETURN OLD;
         END IF;
         IF OLD."Status"=2 AND NEW."Status"=3 AND NEW."EndReason"=4
            AND NEW."ActualEndedAt" IS NOT NULL
            AND OLD."RoomId" IS NOT DISTINCT FROM NEW."RoomId"
            AND OLD."MonthlyRent" IS NOT DISTINCT FROM NEW."MonthlyRent"
            AND OLD."DepositAmount" IS NOT DISTINCT FROM NEW."DepositAmount"
            AND OLD."ContractNumber" IS NOT DISTINCT FROM NEW."ContractNumber"
            AND OLD."StartDate" IS NOT DISTINCT FROM NEW."StartDate"
            AND OLD."EndDate" IS NOT DISTINCT FROM NEW."EndDate"
            AND OLD."Notes" IS NOT DISTINCT FROM NEW."Notes"
            AND OLD."IsDeleted" IS NOT DISTINCT FROM NEW."IsDeleted" THEN
           RETURN NEW;
         END IF;
         IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                      WHERE "SourceContractId"=OLD."Id")
            AND (NEW."Status" IS DISTINCT FROM OLD."Status"
              OR NEW."RoomId" IS DISTINCT FROM OLD."RoomId"
              OR NEW."MonthlyRent" IS DISTINCT FROM OLD."MonthlyRent"
              OR NEW."DepositAmount" IS DISTINCT FROM OLD."DepositAmount"
              OR NEW."ActualEndedAt" IS DISTINCT FROM OLD."ActualEndedAt"
              OR NEW."EndReason" IS DISTINCT FROM OLD."EndReason"
              OR NEW."StartDate" IS DISTINCT FROM OLD."StartDate"
              OR NEW."EndDate" IS DISTINCT FROM OLD."EndDate"
              OR NEW."Notes" IS DISTINCT FROM OLD."Notes"
              OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted") THEN
           RAISE EXCEPTION 'Mid-month source finance and occupancy are immutable'
            USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $freeze$;
        CREATE TRIGGER "TR_BM_2HMid_FreezeSource"
         BEFORE UPDATE OR DELETE ON "BuildingManagementContracts"
         FOR EACH ROW EXECUTE FUNCTION "BM_Freeze2HMidCompletedSource"();
        
        -- Prevent silent rewriting of new-contract first-month charge after issue.
        CREATE FUNCTION "BM_Freeze2HMidTransferCashAndRoster"()
        RETURNS trigger LANGUAGE plpgsql AS $freeze$
        BEGIN
         IF TG_TABLE_NAME='BuildingManagementDepositSettlements' THEN
          IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                     WHERE "CarryForwardSettlementId"=OLD."Id") THEN
           RAISE EXCEPTION 'Transferred mid-month cash settlement cannot be rewritten'
            USING ERRCODE='23514';
          END IF;
         ELSIF TG_TABLE_NAME='BuildingManagementContractTenants' THEN
          IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                     WHERE "SourceContractId"=OLD."ContractId"
                        OR "TargetContractId"=OLD."ContractId") THEN
           RAISE EXCEPTION 'Transferred mid-month occupant roster is immutable'
            USING ERRCODE='23514';
          END IF;
         END IF;
         IF TG_OP='DELETE' THEN RETURN OLD; END IF;
         RETURN NEW;
        END;
        $freeze$;
        CREATE TRIGGER "TR_BM_2HMid_FreezeCarry"
         BEFORE UPDATE OR DELETE ON "BuildingManagementDepositSettlements"
         FOR EACH ROW EXECUTE FUNCTION "BM_Freeze2HMidTransferCashAndRoster"();
        CREATE TRIGGER "TR_BM_2HMid_FreezeRoster"
         BEFORE UPDATE OR DELETE ON "BuildingManagementContractTenants"
         FOR EACH ROW EXECUTE FUNCTION "BM_Freeze2HMidTransferCashAndRoster"();
        
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        
        LOCK TABLE "BuildingManagementRoomChangeMidMonthCompletions"
         IN ACCESS EXCLUSIVE MODE;
        DO $retained$
        BEGIN
         IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                      LIMIT 1) THEN
          RAISE EXCEPTION 'Refuse to discard completed mid-month money/room history'
            USING ERRCODE='23514';
         END IF;
        END;
        $retained$;
        DROP TRIGGER "TR_BM_2HMid_FreezeRoster"
         ON "BuildingManagementContractTenants";
        DROP TRIGGER "TR_BM_2HMid_FreezeCarry"
         ON "BuildingManagementDepositSettlements";
        DROP FUNCTION "BM_Freeze2HMidTransferCashAndRoster"();
        DROP TRIGGER "TR_BM_2HMid_FreezeSource"
         ON "BuildingManagementContracts";
        DROP FUNCTION "BM_Freeze2HMidCompletedSource"();
        DROP TRIGGER "TR_BM_2HMid_TargetInvoiceProration"
         ON "BuildingManagementMonthlyInvoiceCandidates";
        DROP FUNCTION "BM_Require2HMidTargetFirstMonthProration"();
        DROP TRIGGER "TR_BM_2HMidComplete_MutualExclusion"
         ON "BuildingManagementRoomChangeCompletions";
        DROP FUNCTION "BM_Guard2HMidOldLaneDoubleCompletion"();
        DROP TRIGGER "TR_BM_2HMidComplete_SourceDisposition"
         ON "BuildingManagementRoomChangeMidMonthCompletions";
        DROP TRIGGER "TR_BM_2HMidComplete_CommitGate"
         ON "BuildingManagementRoomChangeMidMonthCompletions";
        DROP FUNCTION "BM_Verify2HMidMonthCommit"();
        DROP TRIGGER "TR_BM_2HMidComplete_NoTruncate"
         ON "BuildingManagementRoomChangeMidMonthCompletions";
        DROP FUNCTION "BM_NoTruncate2HMidComplete"();
        DROP TRIGGER "TR_BM_2HMidComplete_Immutable"
         ON "BuildingManagementRoomChangeMidMonthCompletions";
        DROP FUNCTION "BM_Freeze2HMidMonthCompletion"();
        DROP TABLE "BuildingManagementRoomChangeMidMonthCompletions";
        -- Existing functions MUST be restored to their old form; this migration
        -- cannot be downgraded without the original prior code (installed below).
        CREATE OR REPLACE FUNCTION "BM_VerifyRoomChangeTerminalRequest"()
        RETURNS trigger LANGUAGE plpgsql AS $terminal$
        BEGIN
         IF NEW."Status"=4 AND NOT EXISTS (
           SELECT 1 FROM "BuildingManagementRoomChangeCompletions"
           WHERE "RequestId"=NEW."Id"
         ) THEN
          RAISE EXCEPTION 'Completed request requires atomic transfer ledger'
            USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $terminal$;
        CREATE OR REPLACE FUNCTION "BM_RequireAtomicRoomChangeAtContractEnd"()
        RETURNS trigger LANGUAGE plpgsql AS $transfer_end$
        BEGIN
         IF OLD."Status"=2 AND NEW."Status"=3 AND NEW."EndReason"=4
           AND NOT EXISTS (
             SELECT 1 FROM "BuildingManagementRoomChangeCompletions" c
              JOIN "BuildingManagementRoomChangeRequests" r ON r."Id"=c."RequestId"
             WHERE c."SourceContractId"=NEW."Id" AND c."SourceRoomId"=OLD."RoomId"
               AND c."SourceContractId"=r."SourceContractId"
               AND c."SourceRoomId"=r."SourceRoomId"
               AND c."TargetRoomId"=r."TargetRoomId"
               AND c."TargetContractId"<>NEW."Id"
               AND r."Status"=4
               AND r."ReviewedByUserId"=c."CompletedByUserId"
               AND r."ReviewedAtUtc"=c."CompletedAtUtc"
            ) THEN
          RAISE EXCEPTION 'RoomChanged Contract.End requires same-transaction verified atomic transfer'
           USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $transfer_end$;
        
        """);
}
