using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>Signed destination, 7-business-day policy and failed manual transfers.
/// No external payment action; only independent reviewed evidence permits closeout.</summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102022000_Phase2G4RefundInstructionsAndFailedTransfers")]
public sealed class Phase2G4RefundInstructionsAndFailedTransfers : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementContractDepositRefundInstructions" (
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL UNIQUE REFERENCES "BuildingManagementContracts"("Id"),
          "MoveOutRequestId" uuid NOT NULL UNIQUE REFERENCES "BuildingManagementMoveOutRequests"("Id"),
          "DecisionId" uuid NOT NULL UNIQUE REFERENCES
            "BuildingManagementContractDepositSettlementDecisions"("Id"),
          "BuildingId" uuid NOT NULL,
          "OwnerId" uuid NOT NULL,
          "ApprovedAtUtc" timestamptz NOT NULL,
          "ApprovedRefundAmountVnd" numeric(18,0) NOT NULL CHECK ("ApprovedRefundAmountVnd">0),
          "PayeeName" varchar(200) NOT NULL,
          "PayeeBank" varchar(100) NOT NULL,
          "PayeeAccount" varchar(40) NOT NULL,
          "SignedDestinationReference" varchar(500) NOT NULL,
          "HolidayCalendarReference" varchar(500) NOT NULL,
          "DueOn" date NOT NULL,
          CONSTRAINT "CK_BM_RefundDestinationFields" CHECK (
            length(btrim("PayeeName")) BETWEEN 3 AND 200 AND
            length(btrim("PayeeBank")) BETWEEN 3 AND 100 AND
            length(btrim("PayeeAccount")) BETWEEN 6 AND 40 AND
            length(btrim("SignedDestinationReference")) BETWEEN 12 AND 500 AND
            length(btrim("HolidayCalendarReference")) BETWEEN 12 AND 500 AND
            "DueOn" >= ("ApprovedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
        );
        CREATE TABLE "BuildingManagementContractDepositRefundFailedTransfers" (
          "Id" uuid PRIMARY KEY,
          "InstructionId" uuid NOT NULL REFERENCES
            "BuildingManagementContractDepositRefundInstructions"("Id"),
          "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id"),
          "OwnerId" uuid NOT NULL,
          "Reference" varchar(128) NOT NULL,
          "FailedAtUtc" timestamptz NOT NULL,
          "Reason" varchar(500) NOT NULL,
          "BankEvidenceReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_FailedRefundProof" CHECK (
            length(btrim("Reference")) BETWEEN 8 AND 128 AND
            length(btrim("Reason")) BETWEEN 12 AND 500 AND
            length(btrim("BankEvidenceReference")) BETWEEN 12 AND 500)
        );
        CREATE UNIQUE INDEX "UX_BM_FailedRefundRef"
          ON "BuildingManagementContractDepositRefundFailedTransfers"
            (lower(btrim("Reference")));

        ALTER TABLE "BuildingManagementBankEvidenceClaims"
          DROP CONSTRAINT "BuildingManagementBankEvidenceClaims_SourceKind_check";
        ALTER TABLE "BuildingManagementBankEvidenceClaims"
          ADD CONSTRAINT "BuildingManagementBankEvidenceClaims_SourceKind_check"
            CHECK ("SourceKind" IN ('Payment','RefundReview','FailedRefund'));

        CREATE FUNCTION "BM_GuardRefundInstructionOperations"()
        RETURNS trigger LANGUAGE plpgsql AS $ops$
        DECLARE signed record;
        DECLARE instruction record;
        BEGIN
          IF TG_OP <> 'INSERT' THEN
            RAISE EXCEPTION 'Refund instruction/failed transfer history is immutable'
              USING ERRCODE='23514';
          END IF;
          IF current_setting('transaction_isolation') <> 'serializable' THEN
            RAISE EXCEPTION 'Refund operations require serializable transaction'
              USING ERRCODE='23514';
          END IF;
          IF TG_TABLE_NAME='BuildingManagementContractDepositRefundInstructions' THEN
            PERFORM 1 FROM "BuildingManagementContracts"
             WHERE "Id"=NEW."ContractId" FOR UPDATE;
            IF NOT FOUND THEN
              RAISE EXCEPTION 'Refund instruction must lock contract'
                USING ERRCODE='23514';
            END IF;
            SELECT * INTO signed FROM "BuildingManagementContractDepositSettlementDecisions"
             WHERE "Id"=NEW."DecisionId" AND "ContractId"=NEW."ContractId"
               AND "MoveOutRequestId"=NEW."MoveOutRequestId"
               AND "BuildingId"=NEW."BuildingId" FOR SHARE;
            IF signed."Id" IS NULL OR
                signed."ApprovedByUserId" IS DISTINCT FROM NEW."OwnerId" OR
                signed."ApprovedRefundAmount" IS DISTINCT FROM
                  NEW."ApprovedRefundAmountVnd" OR
                signed."ApprovedRefundAmount" <= 0 OR
                NEW."ApprovedAtUtc" < signed."ApprovedAtUtc" OR
                NEW."ApprovedAtUtc" > clock_timestamp()
            THEN
              RAISE EXCEPTION 'Refund destination lacks signed owner amount'
                USING ERRCODE='23514';
            END IF;
          ELSE
            SELECT * INTO instruction FROM
              "BuildingManagementContractDepositRefundInstructions"
              WHERE "Id"=NEW."InstructionId" AND "ContractId"=NEW."ContractId"
              FOR SHARE;
            IF instruction."Id" IS NULL OR
               instruction."OwnerId" IS DISTINCT FROM NEW."OwnerId" OR
               NEW."FailedAtUtc" < instruction."ApprovedAtUtc" OR
               NEW."FailedAtUtc" > clock_timestamp() OR
               EXISTS (SELECT 1 FROM
                 "BuildingManagementContractDepositRefundBankReviews"
                 WHERE "ContractId"=NEW."ContractId")
            THEN
              RAISE EXCEPTION 'Failed transfer requires matching pending owner instruction'
                USING ERRCODE='23514';
            END IF;
            INSERT INTO "BuildingManagementBankEvidenceClaims"
              ("NormalizedReference","SourceKind","SourceId")
            VALUES (lower(btrim(NEW."Reference")),'FailedRefund',NEW."Id")
            ON CONFLICT ("NormalizedReference") DO NOTHING;
            IF NOT EXISTS (SELECT 1 FROM "BuildingManagementBankEvidenceClaims"
              WHERE "NormalizedReference"=lower(btrim(NEW."Reference"))
                AND "SourceKind"='FailedRefund' AND "SourceId"=NEW."Id")
            THEN
              RAISE EXCEPTION 'Failed bank reference was already claimed'
                USING ERRCODE='23514';
            END IF;
          END IF;
          RETURN NEW;
        END; $ops$;
        CREATE TRIGGER "TR_BM_RefundInstructionImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositRefundInstructions"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardRefundInstructionOperations"();
        CREATE TRIGGER "TR_BM_RefundFailureImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositRefundFailedTransfers"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardRefundInstructionOperations"();

        CREATE FUNCTION "BM_RequireRefundInstructionForBankReview"()
        RETURNS trigger LANGUAGE plpgsql AS $signed$
        BEGIN
          IF NOT EXISTS (
              SELECT 1 FROM "BuildingManagementContractDepositRefundInstructions" i
               WHERE i."DecisionId"=NEW."DepositDecisionId"
                 AND i."ContractId"=NEW."ContractId"
                 AND i."MoveOutRequestId"=NEW."MoveOutRequestId"
                 AND i."ApprovedRefundAmountVnd"=NEW."VerifiedRefundAmountVnd"
                 AND NEW."PaidAtUtc">=i."ApprovedAtUtc"
                 FOR SHARE)
          THEN
            RAISE EXCEPTION 'Refund bank evidence lacks approved beneficiary instruction'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END; $signed$;
        CREATE TRIGGER "TR_BM_RefundInstructionRequiredAtReview"
          BEFORE INSERT ON "BuildingManagementContractDepositRefundBankReviews"
          FOR EACH ROW EXECUTE FUNCTION "BM_RequireRefundInstructionForBankReview"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementContractDepositRefundInstructions",
          "BuildingManagementContractDepositRefundFailedTransfers",
          "BuildingManagementContractDepositRefundBankReviews"
          IN ACCESS EXCLUSIVE MODE;
        DO $down$
        BEGIN
          IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositRefundInstructions")
             OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositRefundFailedTransfers")
             OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositRefundBankReviews")
          THEN
            RAISE EXCEPTION 'Signed refund history prevents unsafe downgrade'
              USING ERRCODE='23514';
          END IF;
        END; $down$;
        DROP TRIGGER "TR_BM_RefundInstructionRequiredAtReview"
          ON "BuildingManagementContractDepositRefundBankReviews";
        DROP FUNCTION "BM_RequireRefundInstructionForBankReview"();
        DROP TRIGGER "TR_BM_RefundInstructionImmutable"
          ON "BuildingManagementContractDepositRefundInstructions";
        DROP TRIGGER "TR_BM_RefundFailureImmutable"
          ON "BuildingManagementContractDepositRefundFailedTransfers";
        DROP FUNCTION "BM_GuardRefundInstructionOperations"();
        DROP TABLE "BuildingManagementContractDepositRefundFailedTransfers";
        DROP TABLE "BuildingManagementContractDepositRefundInstructions";
        ALTER TABLE "BuildingManagementBankEvidenceClaims"
          DROP CONSTRAINT "BuildingManagementBankEvidenceClaims_SourceKind_check";
        ALTER TABLE "BuildingManagementBankEvidenceClaims"
          ADD CONSTRAINT "BuildingManagementBankEvidenceClaims_SourceKind_check"
            CHECK ("SourceKind" IN ('Payment','RefundReview'));
        """);
}
