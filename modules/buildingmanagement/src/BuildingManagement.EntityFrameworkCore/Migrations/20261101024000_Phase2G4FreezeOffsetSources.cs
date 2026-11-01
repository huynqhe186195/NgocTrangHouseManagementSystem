using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// After a double-ledger deposit credit posts, its original invoice,
/// independent bank receipt and deposit source can no longer be rewritten or
/// silently receive a second payment. Future corrections require their own
/// approved compensating ledger, never direct mutation of historical rows.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101024000_Phase2G4FreezeOffsetSources")]
public sealed class Phase2G4FreezeOffsetSources : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            CREATE FUNCTION "BM_BlockPostedOffsetSourceMutation"()
            RETURNS trigger LANGUAGE plpgsql AS $frozen$
            DECLARE new_data jsonb;
            DECLARE old_data jsonb;
            DECLARE new_contract uuid;
            DECLARE old_contract uuid;
            DECLARE new_issued uuid;
            DECLARE old_issued uuid;
            DECLARE new_posting uuid;
            DECLARE old_posting uuid;
            DECLARE new_payment uuid;
            DECLARE old_payment uuid;
            BEGIN
              new_data := CASE WHEN TG_OP='DELETE' THEN '{}'::jsonb ELSE to_jsonb(NEW) END;
              old_data := CASE WHEN TG_OP='INSERT' THEN '{}'::jsonb ELSE to_jsonb(OLD) END;
              new_contract := COALESCE(NULLIF(new_data->>'ContractId','')::uuid,
                NULLIF(new_data->>'SourceContractId','')::uuid,
                NULLIF(new_data->>'TargetContractId','')::uuid);
              old_contract := COALESCE(NULLIF(old_data->>'ContractId','')::uuid,
                NULLIF(old_data->>'SourceContractId','')::uuid,
                NULLIF(old_data->>'TargetContractId','')::uuid);
              new_issued := NULLIF(new_data->>'IssuedInvoiceId','')::uuid;
              old_issued := NULLIF(old_data->>'IssuedInvoiceId','')::uuid;
              new_posting := NULLIF(new_data->>'ReceivablePostingId','')::uuid;
              old_posting := NULLIF(old_data->>'ReceivablePostingId','')::uuid;
              new_payment := NULLIF(new_data->>'Id','')::uuid;
              old_payment := NULLIF(old_data->>'Id','')::uuid;

              IF EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                WHERE x."ContractId" IN (
                    new_contract,old_contract,
                    NULLIF(new_data->>'TargetContractId','')::uuid,
                    NULLIF(old_data->>'TargetContractId','')::uuid,
                    NULLIF(new_data->>'SourceContractId','')::uuid,
                    NULLIF(old_data->>'SourceContractId','')::uuid)
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                JOIN "BuildingManagementOfficialInvoiceReceivables" p
                  ON p."ContractId"=x."ContractId"
                WHERE p."Id" IN (new_posting,old_posting)
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                JOIN "BuildingManagementMonthlyIssuedInvoiceLedger" i
                  ON i."ContractId"=x."ContractId"
                WHERE i."Id" IN (new_issued,old_issued)
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                JOIN "BuildingManagementIssuedInvoiceLedger" i
                  ON i."ContractId"=x."ContractId"
                WHERE i."Id" IN (new_issued,old_issued)
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                JOIN "BuildingManagementDepositSettlements" s
                  ON s."TargetContractId"=x."ContractId"
                WHERE s."PaymentId" IN (new_payment,old_payment)
              ) OR EXISTS (
                SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings" x
                JOIN "BuildingManagementOfficialInvoiceReceivables" p
                  ON p."ContractId"=x."ContractId"
                WHERE p."IssuedInvoiceId" IN (new_issued,old_issued)
              ) THEN
                RAISE EXCEPTION
                  'An internally offset contract requires a separately audited compensating entry; original sources are frozen'
                  USING ERRCODE='23514';
              END IF;
              IF TG_OP='DELETE' THEN RETURN OLD; END IF;
              RETURN NEW;
            END;
            $frozen$;

            -- All of these rows are either original money sources or official
            -- receivable sources; no general-purpose writer may bypass the
            -- one-contract offset journal with a follow-on receipt.
            -- The old no-offset closeout decision and actual refund
            -- approval are incompatible with an already posted offset.
            CREATE TRIGGER "TR_BM_OI04FreezeLegacyDepositDecision"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositSettlementDecisions"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeLegacyCloseoutApproval"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositCloseoutApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeMonthlyInvoiceCandidates"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementMonthlyInvoiceCandidates"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetPayments"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementPayments"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetSettlements"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementDepositSettlements"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetReceiptReviews"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceiptReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetMonthlyLineCredits"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetOfficialPostings"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementOfficialInvoiceReceivables"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetMonthlyIssued"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementMonthlyIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetFinalIssued"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetMonthlyAdjustments"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementMonthlyInvoiceAdjustments"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetMonthlyAdjustmentDecisions"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtOffsetPostings"
              IN ACCESS EXCLUSIVE MODE;
            DO $history$
            BEGIN
              IF EXISTS (SELECT 1 FROM
                  "BuildingManagementContractDepositDebtOffsetPostings" LIMIT 1)
              THEN
                RAISE EXCEPTION 'Cannot reopen source mutation after actual cash/debt offset'
                  USING ERRCODE='23514';
              END IF;
            END;
            $history$;
            DROP TRIGGER "TR_BM_OI04FreezeLegacyDepositDecision"
              ON "BuildingManagementContractDepositSettlementDecisions";
            DROP TRIGGER "TR_BM_OI04FreezeLegacyCloseoutApproval"
              ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP TRIGGER "TR_BM_OI04FreezeMonthlyInvoiceCandidates"
              ON "BuildingManagementMonthlyInvoiceCandidates";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetPayments"
              ON "BuildingManagementPayments";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetSettlements"
              ON "BuildingManagementDepositSettlements";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetReceiptReviews"
              ON "BuildingManagementOfficialInvoiceReceiptReviews";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetMonthlyLineCredits"
              ON "BuildingManagementOfficialMonthlyReceiptLineAllocations";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetOfficialPostings"
              ON "BuildingManagementOfficialInvoiceReceivables";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetMonthlyIssued"
              ON "BuildingManagementMonthlyIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetFinalIssued"
              ON "BuildingManagementIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetMonthlyAdjustments"
              ON "BuildingManagementMonthlyInvoiceAdjustments";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetMonthlyAdjustmentDecisions"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            DROP FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            """);
    }
}
