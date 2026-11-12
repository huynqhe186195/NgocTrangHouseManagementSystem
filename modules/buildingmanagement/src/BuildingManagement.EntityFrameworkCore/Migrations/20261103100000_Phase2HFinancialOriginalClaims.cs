using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Commit-time immutable D03 one-adjustment-one-posting protection;
/// revalidate original reviewer independence, and D04 original bank/owner
/// attestation. No contract/room or refund writer is enabled.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103100000_Phase2HFinancialOriginalClaims")]
public sealed class Phase2HFinancialOriginalClaims : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        
        -- A repeated D03 decision under a NEW archive ID must never post twice.
        -- Fail safe if an installation already has financial history: audit rather
        -- than creating ambiguous migration backfills.
        DO $legacy$
        BEGIN
         IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
                    WHERE "Kind"=1) THEN
          RAISE EXCEPTION 'Audit existing D03 postings before adding unique original-adjustment claims'
            USING ERRCODE='23514';
         END IF;
        END;
        $legacy$;
        
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         ADD COLUMN "ApprovedAdjustmentId" uuid NULL;
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         ADD CONSTRAINT "FK_BM_2HFin_ApprovedAdjustment" FOREIGN KEY ("ApprovedAdjustmentId")
         REFERENCES "BuildingManagementMonthlyInvoiceAdjustments"("Id") ON DELETE RESTRICT;
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         ADD CONSTRAINT "CK_BM_2HFin_OriginalAdjustmentRequired" CHECK (
          ("Kind"=1 AND "ApprovedAdjustmentId" IS NOT NULL)
          OR ("Kind" IN (2,3) AND "ApprovedAdjustmentId" IS NULL)
         );
        CREATE UNIQUE INDEX "UX_BM_2HFin_OriginalAdjustment"
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings"("ApprovedAdjustmentId")
         WHERE "ApprovedAdjustmentId" IS NOT NULL;
        
        CREATE FUNCTION "BM_GuardRoomChangeMidMonthOriginalClaims"()
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
        
        -- Alphabetical trigger order means the original ledger validator runs first;
        -- both must accept before an entry is inserted.
        CREATE TRIGGER "TR_BM_2HFin_OriginalClaimIntegrity"
         BEFORE INSERT OR UPDATE OR DELETE
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings"
         FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeMidMonthOriginalClaims"();
        
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        
        LOCK TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         IN ACCESS EXCLUSIVE MODE;
        DO $retention$
        BEGIN
         IF EXISTS (
          SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" LIMIT 1
         ) THEN
          RAISE EXCEPTION 'Refuse weakening D03/D04 original claim proof with financial history'
           USING ERRCODE='23514';
         END IF;
        END;
        $retention$;
        DROP TRIGGER "TR_BM_2HFin_OriginalClaimIntegrity"
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings";
        DROP FUNCTION "BM_GuardRoomChangeMidMonthOriginalClaims"();
        DROP INDEX "UX_BM_2HFin_OriginalAdjustment";
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         DROP CONSTRAINT "CK_BM_2HFin_OriginalAdjustmentRequired";
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         DROP CONSTRAINT "FK_BM_2HFin_ApprovedAdjustment";
        ALTER TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         DROP COLUMN "ApprovedAdjustmentId";
        
        """);
}
