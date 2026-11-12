using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Repair a three-party separation deadlock. A single BootstrapOwner must
/// propose and be the only authenticated SQL poster for D03, whereas the
/// independent decision reviewer and category classifier MUST remain two
/// distinct building-scoped operators. The previous SQL trigger inadvertently
/// forbade the Root that the HTTP writer explicitly requires. Restore
/// write feasibility only for that original proposer, never for staff.
/// Existing D03 history and all immutable journal triggers remain intact.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104210000_Phase2HD03RootPostingAfterIndependentReviews")]
public sealed class Phase2HD03RootPostingAfterIndependentReviews : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        -- Root is the signed original proposer and the only trusted
        -- authenticated poster. Decision reviewer and category classifier
        -- must be two OTHER independent authenticated operators.
        -- No UPDATE/DELETE, refund or bank receipt capability is enabled.
        CREATE OR REPLACE FUNCTION "BM_GuardRoomChangeMidMonthOriginalClaims"()
        RETURNS trigger LANGUAGE plpgsql AS $proof$
        DECLARE archive record; proposal record; decision record;
                source_payment record; payload jsonb;
                owner_id uuid; reviewer_id uuid; ref text;
        BEGIN
         IF TG_OP <> 'INSERT' THEN
           RAISE EXCEPTION 'D03/D04 original financial claims cannot be modified'
             USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
           RAISE EXCEPTION 'Original financial claims require SERIALIZABLE'
             USING ERRCODE='23514';
         END IF;
         SELECT * INTO archive
           FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."ArchiveId" FOR SHARE;
         IF archive."Id" IS NULL
           OR archive."RequestId" IS DISTINCT FROM NEW."RequestId"
           OR archive."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
           OR archive."BuildingId" IS DISTINCT FROM NEW."BuildingId"
         THEN
           RAISE EXCEPTION 'Unscoped D03/D04 financial claim'
             USING ERRCODE='23514';
         END IF;
         payload:=archive."EvidenceJson"::jsonb;
         IF NEW."Kind"=1 THEN
           SELECT * INTO proposal FROM "BuildingManagementMonthlyInvoiceAdjustments"
            WHERE "Id"=NEW."ApprovedAdjustmentId" FOR SHARE;
           SELECT * INTO decision FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
            WHERE "AdjustmentId"=NEW."ApprovedAdjustmentId" FOR SHARE;
           IF archive."Kind"<>2
             OR proposal."Id" IS NULL
             OR decision."Id" IS NULL
             OR decision."Accepted" IS DISTINCT FROM TRUE
             OR decision."Id" IS DISTINCT FROM (payload->>'DecisionId')::uuid
             OR proposal."Id" IS DISTINCT FROM (payload->>'AdjustmentId')::uuid
             OR decision."ApprovedSignedDeltaAmount" IS DISTINCT FROM NEW."SignedAmountVnd"
             OR proposal."SignedDeltaAmount" IS DISTINCT FROM NEW."SignedAmountVnd"
             OR proposal."IssuedInvoiceId" IS DISTINCT FROM archive."ParentEvidenceId"
             OR proposal."ContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR proposal."BuildingId" IS DISTINCT FROM NEW."BuildingId"
             OR decision."ReviewedByUserId" IS NULL
             OR proposal."ProposedByUserId" IS NULL
             OR decision."ReviewedByUserId"=proposal."ProposedByUserId"
             OR archive."ArchivedByUserId" IN
               (proposal."ProposedByUserId",decision."ReviewedByUserId")
             OR NEW."RecordedByUserId" IS DISTINCT FROM proposal."ProposedByUserId"
             OR NEW."RecordedByUserId"=decision."ReviewedByUserId"
             OR NEW."RecordedByUserId"=archive."ArchivedByUserId"
             OR decision."ReviewedAtUtc" IS NULL
             OR decision."ReviewedAtUtc" < proposal."ProposedAtUtc"
             OR archive."ArchivedAtUtc" < decision."ReviewedAtUtc"
             OR payload->>'ClassifiedByUserId'
                IS DISTINCT FROM archive."ArchivedByUserId"::text
           THEN
             RAISE EXCEPTION 'D03 original adjustment/decision or independent signatures invalid'
               USING ERRCODE='23514';
           END IF;
         ELSIF NEW."Kind"=2 THEN
           SELECT * INTO source_payment FROM "BuildingManagementPayments"
            WHERE "Id"=NEW."SourcePaymentId" FOR UPDATE;
           ref:=payload->>'BankStatementReference';
           owner_id:=NULLIF(payload->>'ApprovedByOwnerId','')::uuid;
           reviewer_id:=NULLIF(payload->>'IndependentBankReviewerId','')::uuid;
           IF archive."Kind"<>3
             OR NEW."ApprovedAdjustmentId" IS NOT NULL
             OR source_payment."Id" IS NULL
             OR source_payment."PaidAt" IS NULL
             OR (payload->>'PaidAtUtc')::timestamptz
                  IS DISTINCT FROM source_payment."PaidAt"
             OR reviewer_id IS NULL OR owner_id IS NULL
             OR owner_id=reviewer_id
             OR reviewer_id IS DISTINCT FROM archive."ArchivedByUserId"
             OR NEW."RecordedByUserId" IN (reviewer_id,owner_id)
             OR (payload->>'ReviewedAtUtc')::timestamptz
                 < source_payment."PaidAt"
             OR (payload->>'ReviewedAtUtc')::timestamptz
                 > NEW."RecordedAtUtc"
             OR length(btrim(COALESCE(ref,''))) NOT BETWEEN 12 AND 500
             OR length(btrim(COALESCE(payload->>'SignedBankReviewReference','')))
                 NOT BETWEEN 12 AND 500
             OR length(btrim(COALESCE(payload->>'OwnerApprovedDestinationReference','')))
                 NOT BETWEEN 12 AND 500
           THEN
              RAISE EXCEPTION 'D04 original payment is missing independent bank and owner attestations'
                USING ERRCODE='23514';
           END IF;
         ELSE
            -- D04 outward refund remains forbidden by the earlier hard gate.
            RAISE EXCEPTION 'No authenticated outbound bank settlement lane'
              USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $proof$;
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        -- Never revert approval separation when any D03 financial journal
        -- has been posted; that history is immutable business evidence.
        DO $guard$
        BEGIN
          IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
                     WHERE "Kind"=1 LIMIT 1) THEN
            RAISE EXCEPTION 'Refuse reverting Root D03 writer rules after financial postings'
              USING ERRCODE='23514';
          END IF;
        END;
        $guard$;
        CREATE OR REPLACE FUNCTION "BM_GuardRoomChangeMidMonthOriginalClaims"()
        RETURNS trigger LANGUAGE plpgsql AS $proof$
        DECLARE archive record; proposal record; decision record;
                source_payment record; payload jsonb;
                owner_id uuid; reviewer_id uuid; ref text;
        BEGIN
         IF TG_OP <> 'INSERT' THEN
           RAISE EXCEPTION 'D03/D04 original financial claims cannot be modified'
             USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
           RAISE EXCEPTION 'Original financial claims require SERIALIZABLE'
             USING ERRCODE='23514';
         END IF;
         SELECT * INTO archive
           FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."ArchiveId" FOR SHARE;
         IF archive."Id" IS NULL
           OR archive."RequestId" IS DISTINCT FROM NEW."RequestId"
           OR archive."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
           OR archive."BuildingId" IS DISTINCT FROM NEW."BuildingId"
         THEN
           RAISE EXCEPTION 'Unscoped D03/D04 financial claim'
             USING ERRCODE='23514';
         END IF;
         payload:=archive."EvidenceJson"::jsonb;
         IF NEW."Kind"=1 THEN
           SELECT * INTO proposal FROM "BuildingManagementMonthlyInvoiceAdjustments"
            WHERE "Id"=NEW."ApprovedAdjustmentId" FOR SHARE;
           SELECT * INTO decision FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
            WHERE "AdjustmentId"=NEW."ApprovedAdjustmentId" FOR SHARE;
           IF archive."Kind"<>2
             OR proposal."Id" IS NULL
             OR decision."Id" IS NULL
             OR decision."Accepted" IS DISTINCT FROM TRUE
             OR decision."Id" IS DISTINCT FROM (payload->>'DecisionId')::uuid
             OR proposal."Id" IS DISTINCT FROM (payload->>'AdjustmentId')::uuid
             OR decision."ApprovedSignedDeltaAmount" IS DISTINCT FROM NEW."SignedAmountVnd"
             OR proposal."SignedDeltaAmount" IS DISTINCT FROM NEW."SignedAmountVnd"
             OR proposal."IssuedInvoiceId" IS DISTINCT FROM archive."ParentEvidenceId"
             OR proposal."ContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR proposal."BuildingId" IS DISTINCT FROM NEW."BuildingId"
             OR decision."ReviewedByUserId" IS NULL
             OR proposal."ProposedByUserId" IS NULL
             OR decision."ReviewedByUserId"=proposal."ProposedByUserId"
             OR archive."ArchivedByUserId" IN
               (proposal."ProposedByUserId",decision."ReviewedByUserId")
             OR NEW."RecordedByUserId" IN
               (proposal."ProposedByUserId",decision."ReviewedByUserId")
             OR decision."ReviewedAtUtc" IS NULL
             OR decision."ReviewedAtUtc" < proposal."ProposedAtUtc"
             OR archive."ArchivedAtUtc" < decision."ReviewedAtUtc"
             OR payload->>'ClassifiedByUserId'
                IS DISTINCT FROM archive."ArchivedByUserId"::text
           THEN
             RAISE EXCEPTION 'D03 original adjustment/decision or independent signatures invalid'
               USING ERRCODE='23514';
           END IF;
         ELSIF NEW."Kind"=2 THEN
           SELECT * INTO source_payment FROM "BuildingManagementPayments"
            WHERE "Id"=NEW."SourcePaymentId" FOR UPDATE;
           ref:=payload->>'BankStatementReference';
           owner_id:=NULLIF(payload->>'ApprovedByOwnerId','')::uuid;
           reviewer_id:=NULLIF(payload->>'IndependentBankReviewerId','')::uuid;
           IF archive."Kind"<>3
             OR NEW."ApprovedAdjustmentId" IS NOT NULL
             OR source_payment."Id" IS NULL
             OR source_payment."PaidAt" IS NULL
             OR (payload->>'PaidAtUtc')::timestamptz
                  IS DISTINCT FROM source_payment."PaidAt"
             OR reviewer_id IS NULL OR owner_id IS NULL
             OR owner_id=reviewer_id
             OR reviewer_id IS DISTINCT FROM archive."ArchivedByUserId"
             OR NEW."RecordedByUserId" IN (reviewer_id,owner_id)
             OR (payload->>'ReviewedAtUtc')::timestamptz
                 < source_payment."PaidAt"
             OR (payload->>'ReviewedAtUtc')::timestamptz
                 > NEW."RecordedAtUtc"
             OR length(btrim(COALESCE(ref,''))) NOT BETWEEN 12 AND 500
             OR length(btrim(COALESCE(payload->>'SignedBankReviewReference','')))
                 NOT BETWEEN 12 AND 500
             OR length(btrim(COALESCE(payload->>'OwnerApprovedDestinationReference','')))
                 NOT BETWEEN 12 AND 500
           THEN
              RAISE EXCEPTION 'D04 original payment is missing independent bank and owner attestations'
                USING ERRCODE='23514';
           END IF;
         ELSE
            -- D04 outward refund remains forbidden by the earlier hard gate.
            RAISE EXCEPTION 'No authenticated outbound bank settlement lane'
              USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $proof$;
        """);
}
