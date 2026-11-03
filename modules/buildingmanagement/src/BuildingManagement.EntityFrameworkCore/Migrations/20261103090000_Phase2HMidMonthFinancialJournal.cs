using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace BuildingManagement.Migrations;

/// <summary>
/// Mid-month append-only D03 official receivable journal and D04 reviewed
/// target-deposit posting. Outbound cash remains fail-closed and NO
/// contract/room writer is enabled by this migration.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103090000_Phase2HMidMonthFinancialJournal")]
public sealed class Phase2HMidMonthFinancialJournal : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        
        CREATE TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings" (
         "Id" uuid PRIMARY KEY,
         "RequestId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
         "SourceContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
         "BuildingId" uuid NOT NULL,
         "ArchiveId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeEvidenceArchives"("Id") ON DELETE RESTRICT,
         "OfficialReceivableId" uuid NULL REFERENCES "BuildingManagementOfficialInvoiceReceivables"("Id") ON DELETE RESTRICT,
         "SourcePaymentId" uuid NULL,
         "Kind" integer NOT NULL,
         "SignedAmountVnd" numeric(18,0) NOT NULL,
         "BankTransactionReference" varchar(128) NULL,
         "RecordedByUserId" uuid NOT NULL,
         "RecordedAtUtc" timestamptz NOT NULL,
         CONSTRAINT "CK_BM_2HFinancialPosting_Shape" CHECK (
          "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
          AND "RecordedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
          AND "Kind" IN (1,2,3)
          AND "SignedAmountVnd" <> 0
          AND (("Kind"=1 AND "OfficialReceivableId" IS NOT NULL
                AND "SourcePaymentId" IS NULL AND "BankTransactionReference" IS NULL)
            OR ("Kind"=2 AND "OfficialReceivableId" IS NULL
                AND "SourcePaymentId" IS NOT NULL
                AND length(btrim("BankTransactionReference")) BETWEEN 8 AND 128
                AND "SignedAmountVnd">0)
            OR ("Kind"=3 AND "OfficialReceivableId" IS NULL
                AND "SourcePaymentId" IS NULL
                AND length(btrim("BankTransactionReference")) BETWEEN 8 AND 128
                AND "SignedAmountVnd"<0))
         )
        );
        CREATE UNIQUE INDEX "UX_BM_2HFin_Archive" ON
         "BuildingManagementRoomChangeMidMonthFinancialPostings"("ArchiveId");
        CREATE UNIQUE INDEX "UX_BM_2HFin_OriginalPayment" ON
         "BuildingManagementRoomChangeMidMonthFinancialPostings"("SourcePaymentId")
         WHERE "SourcePaymentId" IS NOT NULL;
        CREATE UNIQUE INDEX "UX_BM_2HFin_BankTx" ON
         "BuildingManagementRoomChangeMidMonthFinancialPostings"("BankTransactionReference")
         WHERE "BankTransactionReference" IS NOT NULL;
        CREATE UNIQUE INDEX "UX_BM_2HFin_OneCashLeg" ON
         "BuildingManagementRoomChangeMidMonthFinancialPostings"("RequestId")
         WHERE "Kind" IN (2,3);
        
        CREATE FUNCTION "BM_GuardRoomChangeMidMonthPosting"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE req record; archive record; receivable record;
          proposal record; approval record; original_invoice record;
          payment record; packet_json jsonb;
          previous_sum numeric; actual_paid numeric; categories_sum numeric;
        BEGIN
         IF TG_OP <> 'INSERT' THEN
          RAISE EXCEPTION 'Mid-month financial history is immutable'
           USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION 'Mid-month posting requires SERIALIZABLE'
           USING ERRCODE='23514';
         END IF;
         SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId" FOR UPDATE;
         SELECT * INTO archive FROM "BuildingManagementRoomChangeEvidenceArchives"
          WHERE "Id"=NEW."ArchiveId" FOR SHARE;
         IF req."Id" IS NULL OR req."Status" NOT IN (0,1)
          OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
          OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
          OR archive."Id" IS NULL
          OR archive."RequestId" IS DISTINCT FROM NEW."RequestId"
          OR archive."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
          OR archive."BuildingId" IS DISTINCT FROM NEW."BuildingId"
          OR NEW."RecordedAtUtc" < archive."ArchivedAtUtc"
          OR NEW."RecordedByUserId" = archive."ArchivedByUserId"
          OR req."RequestedDate" = (
            date_trunc('month',req."RequestedDate"::timestamp)
               + interval '1 month - 1 day')::date
          OR (NEW."RecordedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
               < req."RequestedDate"
          OR NEW."RecordedAtUtc" > clock_timestamp()
         THEN
          RAISE EXCEPTION 'Mid-month posting request, actor or archive scope invalid'
           USING ERRCODE='23514';
         END IF;
        
         packet_json := archive."EvidenceJson"::jsonb;
         -- Financial rows cannot bypass the independently signed day
         -- choice or physical three-party handover of this exact request.
         IF NOT EXISTS (
           SELECT 1 FROM "BuildingManagementRoomChangeEvidenceArchives" a
           WHERE a."RequestId"=NEW."RequestId"
             AND a."SourceContractId"=NEW."SourceContractId"
             AND a."BuildingId"=NEW."BuildingId" AND a."Kind"=1
         ) OR NOT EXISTS (
           SELECT 1 FROM "BuildingManagementRoomChangeEvidenceArchives" a
           WHERE a."RequestId"=NEW."RequestId"
             AND a."SourceContractId"=NEW."SourceContractId"
             AND a."BuildingId"=NEW."BuildingId" AND a."Kind"=4
         ) THEN
           RAISE EXCEPTION 'D01/D02 pricing and D06 handover must be signed first'
             USING ERRCODE='23514';
         END IF;
                 IF NEW."Kind"=1 THEN
          IF archive."Kind" <> 2 THEN
           RAISE EXCEPTION 'D03 requires independently classified source invoice'
            USING ERRCODE='23514';
          END IF;
          SELECT * INTO receivable FROM "BuildingManagementOfficialInvoiceReceivables"
           WHERE "Id"=NEW."OfficialReceivableId" FOR UPDATE;
          SELECT * INTO original_invoice FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
           WHERE "Id"=archive."ParentEvidenceId" FOR SHARE;
          SELECT * INTO proposal FROM "BuildingManagementMonthlyInvoiceAdjustments"
           WHERE "Id"=(packet_json->>'AdjustmentId')::uuid FOR SHARE;
          SELECT * INTO approval FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
           WHERE "Id"=(packet_json->>'DecisionId')::uuid FOR SHARE;
          IF receivable."Id" IS NULL
           OR receivable."Kind" <> 1
           OR receivable."IssuedInvoiceId" IS DISTINCT FROM archive."ParentEvidenceId"
           OR receivable."ContractId" IS DISTINCT FROM req."SourceContractId"
           OR receivable."RoomId" IS DISTINCT FROM req."SourceRoomId"
           OR receivable."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR receivable."SourceEvidenceSha256" IS DISTINCT FROM archive."ParentEvidenceSha256"
           OR original_invoice."Id" IS NULL
           OR original_invoice."CandidateSha256" IS DISTINCT FROM archive."ParentEvidenceSha256"
           OR proposal."Id" IS NULL
           OR proposal."IssuedInvoiceId" IS DISTINCT FROM receivable."IssuedInvoiceId"
           OR proposal."ContractId" IS DISTINCT FROM req."SourceContractId"
           OR proposal."BuildingId" IS DISTINCT FROM req."BuildingId"
           OR approval."Id" IS NULL
           OR approval."AdjustmentId" IS DISTINCT FROM proposal."Id"
           OR approval."Accepted" IS DISTINCT FROM TRUE
           OR approval."ApprovedSignedDeltaAmount" IS DISTINCT FROM NEW."SignedAmountVnd"
           OR NEW."SignedAmountVnd" IS DISTINCT FROM proposal."SignedDeltaAmount"
           OR (packet_json->>'ApprovedSignedDeltaVnd')::numeric IS DISTINCT FROM
             NEW."SignedAmountVnd"
           OR packet_json->>'IssuedInvoiceId' IS DISTINCT FROM
              receivable."IssuedInvoiceId"::text
          THEN
           RAISE EXCEPTION 'D03 actual posted receivable lacks original approved line correction'
            USING ERRCODE='23514';
          END IF;
          SELECT COALESCE(sum((element.value->>'SignedDeltaVnd')::numeric),0)
            INTO categories_sum
            FROM jsonb_array_elements(packet_json->'Lines') AS element(value);
          IF jsonb_typeof(packet_json->'Lines') IS DISTINCT FROM 'array'
             OR jsonb_array_length(packet_json->'Lines') NOT BETWEEN 1 AND 4
             OR EXISTS (
                SELECT 1 FROM jsonb_array_elements(packet_json->'Lines') AS element(value)
                 WHERE (element.value->>'Category')::integer NOT IN (1,2,3,4)
                    OR (element.value->>'SignedDeltaVnd')::numeric = 0
                    OR sign((element.value->>'SignedDeltaVnd')::numeric)
                         <> sign(NEW."SignedAmountVnd")
             )
             OR (SELECT count(DISTINCT (element.value->>'Category')::integer)
                 FROM jsonb_array_elements(packet_json->'Lines') AS element(value))
                   IS DISTINCT FROM jsonb_array_length(packet_json->'Lines')
             OR categories_sum IS DISTINCT FROM NEW."SignedAmountVnd" THEN
           RAISE EXCEPTION 'D03 approved category split is not balanced'
            USING ERRCODE='23514';
          END IF;
          SELECT COALESCE(sum("SignedAmountVnd"),0) INTO previous_sum
           FROM "BuildingManagementRoomChangeMidMonthFinancialPostings"
           WHERE "OfficialReceivableId"=NEW."OfficialReceivableId";
          SELECT COALESCE(sum("Amount"),0) INTO actual_paid
           FROM "BuildingManagementOfficialInvoiceReceiptReviews"
           WHERE "ReceivablePostingId"=NEW."OfficialReceivableId"
            AND "Accepted"=TRUE;
          IF receivable."OriginalAmount"+previous_sum+NEW."SignedAmountVnd"
              < GREATEST(actual_paid,1) THEN
           RAISE EXCEPTION 'D03 adjustment would erase collected funds or official receivable'
             USING ERRCODE='23514';
          END IF;
         ELSIF NEW."Kind"=2 THEN
          IF archive."Kind" <> 3 OR packet_json->>'Leg' NOT IN ('1','VerifiedTopUp')
            OR packet_json->>'OriginalPaymentId' IS DISTINCT FROM NEW."SourcePaymentId"::text
            OR packet_json->>'TransactionReference' IS DISTINCT FROM NEW."BankTransactionReference"
            OR (packet_json->>'ExactAmountVnd')::numeric IS DISTINCT FROM NEW."SignedAmountVnd"
          THEN
           RAISE EXCEPTION 'D04 top-up must match reviewed completed original payment'
            USING ERRCODE='23514';
          END IF;
          SELECT * INTO payment FROM "BuildingManagementPayments"
           WHERE "Id"=NEW."SourcePaymentId" FOR UPDATE;
          IF payment."Id" IS NULL OR payment."Status"<>2
             OR payment."Purpose"<>2
             OR payment."Method"<>2 OR payment."Channel"<>1
             OR payment."DepositDisposition"<>1
             OR payment."Amount" IS DISTINCT FROM NEW."SignedAmountVnd"
             OR payment."InvoiceId" IS NOT NULL
             OR payment."RoomReservationId" IS NOT NULL
             OR payment."PaidAt" IS NULL
             OR payment."PaidAt" > NEW."RecordedAtUtc"
             OR payment."Notes" IS NULL
             OR payment."Notes" NOT LIKE 'BANK-PROOF: %'
             OR (packet_json->>'TargetContractId')::uuid = req."SourceContractId"
             OR payment."ContractId" IS DISTINCT FROM (packet_json->>'TargetContractId')::uuid
             OR payment."TransactionReference" IS DISTINCT FROM NEW."BankTransactionReference"
             OR payment."IsDeleted" IS DISTINCT FROM FALSE
          THEN
            RAISE EXCEPTION 'D04 source cash is not a completed distinct target deposit'
             USING ERRCODE='23514';
          END IF;
         ELSE
          -- A manual outbound bank reference alone is not a settled bank payout.
          -- Deny actual refund posting until a source cash-backed bank journal exists.
          RAISE EXCEPTION 'D04 outward refund not yet bank-settlement authorized'
           USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $guard$;
        CREATE TRIGGER "TR_BM_2HFin_ImmutablePosting"
         BEFORE INSERT OR UPDATE OR DELETE
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings"
         FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeMidMonthPosting"();
        CREATE FUNCTION "BM_NoTruncate2HFinancial"()
         RETURNS trigger LANGUAGE plpgsql AS $denied$
        BEGIN
         RAISE EXCEPTION '2H financial journal cannot be truncated'
          USING ERRCODE='23514';
        END; $denied$;
        CREATE TRIGGER "TR_BM_2HFin_NoTruncate"
         BEFORE TRUNCATE ON "BuildingManagementRoomChangeMidMonthFinancialPostings"
         FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncate2HFinancial"();
        
        """);
    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings"
         IN ACCESS EXCLUSIVE MODE;
        DO $check$
        BEGIN
         IF EXISTS(SELECT 1 FROM "BuildingManagementRoomChangeMidMonthFinancialPostings" LIMIT 1) THEN
          RAISE EXCEPTION 'Cannot delete signed mid-month financial entries'
           USING ERRCODE='23514';
         END IF;
        END; $check$;
        DROP TRIGGER "TR_BM_2HFin_NoTruncate"
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings";
        DROP FUNCTION "BM_NoTruncate2HFinancial"();
        DROP TRIGGER "TR_BM_2HFin_ImmutablePosting"
         ON "BuildingManagementRoomChangeMidMonthFinancialPostings";
        DROP FUNCTION "BM_GuardRoomChangeMidMonthPosting"();
        DROP TABLE "BuildingManagementRoomChangeMidMonthFinancialPostings";
        """);
}
