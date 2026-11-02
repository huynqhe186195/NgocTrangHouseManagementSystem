using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>One case-insensitive bank reference cannot evidence both a receipt and a refund.</summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102021000_Phase2G4GlobalBankEvidenceReferenceClaims")]
public sealed class Phase2G4GlobalBankEvidenceReferenceClaims : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementBankEvidenceClaims" (
          "NormalizedReference" varchar(128) PRIMARY KEY,
          "SourceKind" varchar(20) NOT NULL CHECK ("SourceKind" IN ('Payment','RefundReview')),
          "SourceId" uuid NOT NULL,
          "ClaimedAtUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
          CONSTRAINT "CK_BM_BankRefNormalized" CHECK (
            "NormalizedReference"=lower(btrim("NormalizedReference"))
            AND length("NormalizedReference") BETWEEN 1 AND 128)
        );
        CREATE UNIQUE INDEX "UX_BM_BankEvidenceClaimSource"
          ON "BuildingManagementBankEvidenceClaims" ("SourceKind","SourceId");
        -- Existing duplicated normalized references abort this migration.
        INSERT INTO "BuildingManagementBankEvidenceClaims"
          ("NormalizedReference","SourceKind","SourceId")
          SELECT lower(btrim("TransactionReference")),'Payment',"Id"
            FROM "BuildingManagementPayments"
           WHERE "TransactionReference" IS NOT NULL;
        INSERT INTO "BuildingManagementBankEvidenceClaims"
          ("NormalizedReference","SourceKind","SourceId")
          SELECT lower(btrim("BankTransactionReference")),'RefundReview',"Id"
            FROM "BuildingManagementContractDepositRefundBankReviews";
        CREATE FUNCTION "BM_ClaimGlobalBankEvidence"()
        RETURNS trigger LANGUAGE plpgsql AS $claim$
        DECLARE vref text; vkind text; vid uuid;
        BEGIN
          IF TG_TABLE_NAME='BuildingManagementPayments' THEN
            IF TG_OP='UPDATE' THEN
              IF OLD."TransactionReference" IS NOT NULL AND
                OLD."TransactionReference" IS DISTINCT FROM NEW."TransactionReference"
              THEN
                RAISE EXCEPTION 'Claimed payment reference cannot change'
                  USING ERRCODE='23514';
              END IF;
              IF NEW."TransactionReference" IS NOT DISTINCT FROM OLD."TransactionReference"
              THEN RETURN NEW; END IF;
            END IF;
            IF NEW."TransactionReference" IS NULL THEN RETURN NEW; END IF;
            vref:=lower(btrim(NEW."TransactionReference"));
            vkind:='Payment'; vid:=NEW."Id";
          ELSE
            vref:=lower(btrim(NEW."BankTransactionReference"));
            vkind:='RefundReview'; vid:=NEW."Id";
          END IF;
          IF vref IS NULL OR length(vref) NOT BETWEEN 1 AND 128
             OR vid IS NULL
          THEN
            RAISE EXCEPTION 'Invalid shared bank reference' USING ERRCODE='23514';
          END IF;
          INSERT INTO "BuildingManagementBankEvidenceClaims"
            ("NormalizedReference","SourceKind","SourceId")
          VALUES (vref,vkind,vid)
          ON CONFLICT ("NormalizedReference") DO NOTHING;
          IF NOT EXISTS (SELECT 1 FROM "BuildingManagementBankEvidenceClaims"
             WHERE "NormalizedReference"=vref AND "SourceKind"=vkind
               AND "SourceId"=vid)
          THEN
            RAISE EXCEPTION 'Bank reference already used for another transaction'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END; $claim$;
        CREATE TRIGGER "TR_BM_ClaimIncomingBankReference"
          BEFORE INSERT OR UPDATE OF "TransactionReference"
          ON "BuildingManagementPayments"
          FOR EACH ROW EXECUTE FUNCTION "BM_ClaimGlobalBankEvidence"();
        CREATE TRIGGER "TR_BM_ClaimOutgoingBankReference"
          BEFORE INSERT ON "BuildingManagementContractDepositRefundBankReviews"
          FOR EACH ROW EXECUTE FUNCTION "BM_ClaimGlobalBankEvidence"();

        CREATE FUNCTION "BM_FreezeGlobalBankClaims"()
        RETURNS trigger LANGUAGE plpgsql AS $freeze$
        BEGIN
          RAISE EXCEPTION 'Bank evidence claim history is immutable'
            USING ERRCODE='23514';
        END; $freeze$;
        CREATE TRIGGER "TR_BM_FreezeBankClaimRows"
          BEFORE UPDATE OR DELETE ON "BuildingManagementBankEvidenceClaims"
          FOR EACH ROW EXECUTE FUNCTION "BM_FreezeGlobalBankClaims"();
        CREATE TRIGGER "TR_BM_FreezeBankClaimTruncate"
          BEFORE TRUNCATE ON "BuildingManagementBankEvidenceClaims"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_FreezeGlobalBankClaims"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementBankEvidenceClaims",
          "BuildingManagementContractDepositRefundBankReviews"
          IN ACCESS EXCLUSIVE MODE;
        DO $down$
        BEGIN
          IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositRefundBankReviews")
          THEN
            RAISE EXCEPTION 'Signed refund history prevents bank-claim downgrade'
              USING ERRCODE='23514';
          END IF;
        END; $down$;
        DROP TRIGGER "TR_BM_ClaimIncomingBankReference"
          ON "BuildingManagementPayments";
        DROP TRIGGER "TR_BM_ClaimOutgoingBankReference"
          ON "BuildingManagementContractDepositRefundBankReviews";
        DROP FUNCTION "BM_ClaimGlobalBankEvidence"();
        DROP TRIGGER "TR_BM_FreezeBankClaimRows" ON "BuildingManagementBankEvidenceClaims";
        DROP TRIGGER "TR_BM_FreezeBankClaimTruncate" ON "BuildingManagementBankEvidenceClaims";
        DROP FUNCTION "BM_FreezeGlobalBankClaims"();
        DROP TABLE "BuildingManagementBankEvidenceClaims";
        """);
}
