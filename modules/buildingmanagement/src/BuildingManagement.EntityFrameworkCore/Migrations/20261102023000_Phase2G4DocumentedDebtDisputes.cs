using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>Immutable dispute and independent Owner resolution audit. An open
/// or rejected obligation prevents posting ANY new deposit-debt offset.</summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102023000_Phase2G4DocumentedDebtDisputes")]
public sealed class Phase2G4DocumentedDebtDisputes : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementContractDepositDebtDisputes" (
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id"),
          "MoveOutRequestId" uuid NOT NULL REFERENCES "BuildingManagementMoveOutRequests"("Id"),
          "BuildingId" uuid NOT NULL,
          "OfficialPostingId" uuid NOT NULL REFERENCES
            "BuildingManagementOfficialInvoiceReceivables"("Id"),
          "OpenedByUserId" uuid NOT NULL,
          "OpenedAtUtc" timestamptz NOT NULL,
          "Reason" varchar(500) NOT NULL,
          "EvidenceReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_DebtDisputeSigned" CHECK (
            "OpenedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("Reason")) BETWEEN 12 AND 500
            AND length(btrim("EvidenceReference")) BETWEEN 12 AND 500),
          CONSTRAINT "UX_BM_DebtDisputeClaim" UNIQUE ("ContractId","OfficialPostingId")
        );
        CREATE TABLE "BuildingManagementContractDepositDebtDisputeResolutions" (
          "Id" uuid PRIMARY KEY,
          "DisputeId" uuid NOT NULL UNIQUE REFERENCES
            "BuildingManagementContractDepositDebtDisputes"("Id"),
          "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id"),
          "OwnerId" uuid NOT NULL,
          "ResolvedAtUtc" timestamptz NOT NULL,
          "DebtAccepted" boolean NOT NULL,
          "SignedOwnerResolutionReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_DebtDisputeOwnerProof" CHECK (
            "OwnerId" <> '00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("SignedOwnerResolutionReference")) BETWEEN 12 AND 500)
        );
        CREATE FUNCTION "BM_GuardSignedDebtDisputes"()
        RETURNS trigger LANGUAGE plpgsql AS $dispute$
        DECLARE posting record;
        DECLARE original record;
        BEGIN
          IF TG_OP <> 'INSERT' THEN
            RAISE EXCEPTION 'Signed dispute event is immutable'
              USING ERRCODE='23514';
          END IF;
          IF current_setting('transaction_isolation') <> 'serializable' THEN
            RAISE EXCEPTION 'Debt dispute changes require serializable isolation'
              USING ERRCODE='23514';
          END IF;
          PERFORM 1 FROM "BuildingManagementContracts"
            WHERE "Id"=NEW."ContractId" FOR UPDATE;
          IF NOT FOUND THEN
            RAISE EXCEPTION 'Debt dispute must lock original contract'
              USING ERRCODE='23514';
          END IF;
          IF TG_TABLE_NAME='BuildingManagementContractDepositDebtDisputes' THEN
            SELECT * INTO posting FROM "BuildingManagementOfficialInvoiceReceivables"
              WHERE "Id"=NEW."OfficialPostingId" AND "ContractId"=NEW."ContractId"
                AND "BuildingId"=NEW."BuildingId" FOR SHARE;
            IF posting."Id" IS NULL OR
               NEW."OpenedAtUtc">clock_timestamp() OR
               EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings"
                       WHERE "ContractId"=NEW."ContractId")
            THEN
              RAISE EXCEPTION 'Dispute requires unposted original official debt'
                USING ERRCODE='23514';
            END IF;
          ELSE
            SELECT * INTO original FROM "BuildingManagementContractDepositDebtDisputes"
             WHERE "Id"=NEW."DisputeId" AND "ContractId"=NEW."ContractId"
             FOR SHARE;
            IF original."Id" IS NULL OR
               NEW."ResolvedAtUtc"<original."OpenedAtUtc" OR
               NEW."ResolvedAtUtc">clock_timestamp() OR
               EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtOffsetPostings"
                       WHERE "ContractId"=NEW."ContractId")
            THEN
              RAISE EXCEPTION 'Dispute resolution is not an authorized pending obligation'
                USING ERRCODE='23514';
            END IF;
          END IF;
          RETURN NEW;
        END; $dispute$;
        CREATE TRIGGER "TR_BM_DebtDisputeImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositDebtDisputes"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedDebtDisputes"();
        CREATE TRIGGER "TR_BM_DebtDisputeResolutionImmutable"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDepositDebtDisputeResolutions"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedDebtDisputes"();
        CREATE FUNCTION "BM_NoOpenDebtDisputeAtOffsetPost"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM "BuildingManagementContractDepositDebtDisputes" d
            LEFT JOIN "BuildingManagementContractDepositDebtDisputeResolutions" r
              ON r."DisputeId"=d."Id"
            WHERE d."ContractId"=NEW."ContractId"
              AND (r."Id" IS NULL OR r."DebtAccepted"=FALSE)
          ) THEN
            RAISE EXCEPTION 'Pending or rejected debt cannot be offset against deposit'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END; $guard$;
        CREATE TRIGGER "TR_BM_OI04_NoDisputedDebtOffset"
          BEFORE INSERT ON "BuildingManagementContractDepositDebtOffsetPostings"
          FOR EACH ROW EXECUTE FUNCTION "BM_NoOpenDebtDisputeAtOffsetPost"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementContractDepositDebtDisputes",
          "BuildingManagementContractDepositDebtDisputeResolutions"
          IN ACCESS EXCLUSIVE MODE;
        DO $down$
        BEGIN
          IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtDisputes")
            OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositDebtDisputeResolutions")
          THEN
            RAISE EXCEPTION 'Signed dispute history cannot be downgraded'
              USING ERRCODE='23514';
          END IF;
        END; $down$;
        DROP TRIGGER "TR_BM_OI04_NoDisputedDebtOffset"
          ON "BuildingManagementContractDepositDebtOffsetPostings";
        DROP FUNCTION "BM_NoOpenDebtDisputeAtOffsetPost"();
        DROP TRIGGER "TR_BM_DebtDisputeImmutable"
          ON "BuildingManagementContractDepositDebtDisputes";
        DROP TRIGGER "TR_BM_DebtDisputeResolutionImmutable"
          ON "BuildingManagementContractDepositDebtDisputeResolutions";
        DROP FUNCTION "BM_GuardSignedDebtDisputes"();
        DROP TABLE "BuildingManagementContractDepositDebtDisputeResolutions";
        DROP TABLE "BuildingManagementContractDepositDebtDisputes";
        """);
}
