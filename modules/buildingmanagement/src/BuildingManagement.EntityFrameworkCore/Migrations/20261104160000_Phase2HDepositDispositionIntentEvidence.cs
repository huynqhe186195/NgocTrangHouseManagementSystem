using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// H4 D04 append-only DOCUMENTARY INTENT, never a cash or rental writer.
/// Accounts for unpaid top-up due within one Vietnam calendar month and an
/// EXCLUSIVE future rent-credit or bank-reviewed refund candidate. The
/// claimed original money is NOT verified by the presence of this table.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104160000_Phase2HDepositDispositionIntentEvidence")]
public sealed class Phase2HDepositDispositionIntentEvidence : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeDepositDispositionIntents" (
          "Id" uuid PRIMARY KEY,
          "RequestId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
          "SourceContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "TargetContractId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "TransferDateVietnam" date NOT NULL,
          "ClaimedOriginalSourceHeldVnd" numeric(18,0) NOT NULL,
          "AlreadyPostedApprovedDeductionsVnd" numeric(18,0) NOT NULL,
          "ProposedCarryTargetVnd" numeric(18,0) NOT NULL,
          "TargetRequiredDepositVnd" numeric(18,0) NOT NULL,
          "ProposedUnpaidTopUpVnd" numeric(18,0) NOT NULL,
          "ProposedTopUpDueDateVietnam" date NULL,
          "ProposedFutureRentCreditVnd" numeric(18,0) NOT NULL,
          "ProposedManualRefundVnd" numeric(18,0) NOT NULL,
          "SurplusChoice" integer NOT NULL,
          "OriginalCashLedgerReference" varchar(500) NOT NULL,
          "TargetDepositPolicyReference" varchar(500) NOT NULL,
          "SignedDeductionReference" varchar(500) NULL,
          "OwnerSignedDispositionReference" varchar(500) NOT NULL,
          "ProposedByOwnerId" uuid NOT NULL,
          "ProposedAtUtc" timestamptz NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          CONSTRAINT "CK_BM_2H_H4Intent_Money" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "RequestId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BuildingId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "TargetContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "SourceContractId"<>"TargetContractId"
            AND "ProposedByOwnerId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "ClaimedOriginalSourceHeldVnd">0
            AND "TargetRequiredDepositVnd">0
            AND "AlreadyPostedApprovedDeductionsVnd">=0
            AND "ProposedCarryTargetVnd">=0
            AND "ProposedUnpaidTopUpVnd">=0
            AND "ProposedFutureRentCreditVnd">=0
            AND "ProposedManualRefundVnd">=0
            AND "AlreadyPostedApprovedDeductionsVnd"<=
                "ClaimedOriginalSourceHeldVnd"
            AND "ClaimedOriginalSourceHeldVnd" =
               "AlreadyPostedApprovedDeductionsVnd"
               +"ProposedCarryTargetVnd"
               +"ProposedFutureRentCreditVnd"
               +"ProposedManualRefundVnd"
            AND "TargetRequiredDepositVnd" =
                "ProposedCarryTargetVnd"+"ProposedUnpaidTopUpVnd"
            AND "SurplusChoice" IN (0,1,2)
            AND (
               ("SurplusChoice"=0 AND "ProposedFutureRentCreditVnd"=0
                 AND "ProposedManualRefundVnd"=0)
              OR
               ("SurplusChoice"=1 AND "ProposedFutureRentCreditVnd">0
                 AND "ProposedManualRefundVnd"=0)
              OR
               ("SurplusChoice"=2 AND "ProposedManualRefundVnd">0
                 AND "ProposedFutureRentCreditVnd"=0)
            )
            AND (
               ("ProposedUnpaidTopUpVnd"=0
                 AND "ProposedTopUpDueDateVietnam" IS NULL)
               OR
               ("ProposedUnpaidTopUpVnd">0 AND
                  "ProposedTopUpDueDateVietnam" BETWEEN
                    "TransferDateVietnam" AND
                    ("TransferDateVietnam" + interval '1 month')::date
                  AND "SurplusChoice"=0)
            )
            AND length(btrim("OriginalCashLedgerReference")) BETWEEN 12 AND 500
            AND length(btrim("TargetDepositPolicyReference")) BETWEEN 12 AND 500
            AND length(btrim("OwnerSignedDispositionReference")) BETWEEN 12 AND 500
            AND (("AlreadyPostedApprovedDeductionsVnd"=0
                     AND "SignedDeductionReference" IS NULL)
                OR ("AlreadyPostedApprovedDeductionsVnd">0
                     AND length(btrim("SignedDeductionReference")) BETWEEN 12 AND 500))
            AND "EvidenceSha256"~'^[0-9a-f]{64}$'
          )
        );
        CREATE UNIQUE INDEX "UX_BM_H4Intent_Request"
          ON "BuildingManagementRoomChangeDepositDispositionIntents"("RequestId");
        CREATE INDEX "IX_BM_H4Intent_BuildingSourceDate"
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
           ("BuildingId","SourceContractId","TransferDateVietnam");

        CREATE FUNCTION "BM_2HVerifyH4IntentSource"()
        RETURNS trigger LANGUAGE plpgsql AS $h4$
        DECLARE original record;
        BEGIN
          IF current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'H4 disposition intent requires SERIALIZABLE'
             USING ERRCODE='23514';
          END IF;
          SELECT "Id","Status","SourceContractId","BuildingId",
                 "RequestedDate" INTO original
            FROM "BuildingManagementRoomChangeRequests"
           WHERE "Id"=NEW."RequestId" FOR SHARE;
          IF original."Id" IS NULL OR original."Status" NOT IN (0,1)
             OR original."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR original."BuildingId" IS DISTINCT FROM NEW."BuildingId"
             OR original."RequestedDate" IS DISTINCT FROM NEW."TransferDateVietnam"
             OR EXISTS(
                SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
                 WHERE "RequestId"=NEW."RequestId")
          THEN
            RAISE EXCEPTION
             'H4 proposal must match a still-open original same-building request'
             USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $h4$;
        CREATE TRIGGER "TR_BM_2H_H4Intent_Source"
          BEFORE INSERT
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HVerifyH4IntentSource"();

        CREATE FUNCTION "BM_2HDenyH4IntentMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $immutable$
        BEGIN
          RAISE EXCEPTION 'H4 source disposition intent is immutable'
            USING ERRCODE='23514';
        END;
        $immutable$;
        CREATE TRIGGER "TR_BM_2H_H4Intent_Immutable"
          BEFORE UPDATE OR DELETE
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HDenyH4IntentMutation"();
        CREATE TRIGGER "TR_BM_2H_H4Intent_NoTruncate"
          BEFORE TRUNCATE
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_2HDenyH4IntentMutation"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDepositDispositionIntents"
          IN ACCESS EXCLUSIVE MODE;
        DO $retain$
        BEGIN
          IF EXISTS(
            SELECT 1 FROM "BuildingManagementRoomChangeDepositDispositionIntents"
            LIMIT 1) THEN
            RAISE EXCEPTION 'Cannot discard approved H4 disposition intent history'
              USING ERRCODE='23514';
          END IF;
        END;
        $retain$;
        DROP TABLE "BuildingManagementRoomChangeDepositDispositionIntents";
        DROP FUNCTION "BM_2HVerifyH4IntentSource"();
        DROP FUNCTION "BM_2HDenyH4IntentMutation"();
        """);
}
