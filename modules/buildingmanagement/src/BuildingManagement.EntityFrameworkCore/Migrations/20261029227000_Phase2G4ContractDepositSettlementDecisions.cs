using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2G.4 independently approved deposit decision, not an outgoing money
/// ledger and not a replacement for existing DepositSettlement. Approval
/// freezes held/refund/forfeit amounts; the 2G.5 closeout must match them.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029227000_Phase2G4ContractDepositSettlementDecisions")]
public sealed class Phase2G4ContractDepositSettlementDecisions : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementContractDepositSettlementDecisions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id")
                    ON DELETE RESTRICT,
                "MoveOutRequestId" uuid NOT NULL
                    REFERENCES "BuildingManagementMoveOutRequests"("Id") ON DELETE RESTRICT,
                "FinalIssuedInvoiceId" uuid NOT NULL
                    REFERENCES "BuildingManagementIssuedInvoiceLedger"("Id") ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "ApprovedByUserId" uuid NOT NULL,
                "ApprovedAtUtc" timestamptz NOT NULL,
                "VerifiedHeldAmount" numeric(18,0) NOT NULL,
                "ApprovedForfeitAmount" numeric(18,0) NOT NULL,
                "ApprovedRefundAmount" numeric(18,0) NOT NULL,
                "ApprovedDebtOffsetAmount" numeric(18,0) NOT NULL DEFAULT 0,
                "DamageReason" varchar(500) NULL,
                "DamageEvidenceReference" varchar(500) NULL,
                "ExplicitDepositPolicyReference" varchar(200) NOT NULL,
                "ApprovalJson" text NOT NULL,
                "ApprovalSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_DepositDecision_Amounts" CHECK (
                    "VerifiedHeldAmount" > 0
                    AND "ApprovedForfeitAmount" >= 0
                    AND "ApprovedRefundAmount" >= 0
                    AND "ApprovedDebtOffsetAmount" = 0
                    AND "VerifiedHeldAmount" =
                        "ApprovedForfeitAmount" + "ApprovedRefundAmount"
                ),
                CONSTRAINT "CK_BM_DepositDecision_Damage" CHECK (
                    ("ApprovedForfeitAmount" = 0 AND "DamageReason" IS NULL
                        AND "DamageEvidenceReference" IS NULL) OR
                    ("ApprovedForfeitAmount" > 0
                        AND length(btrim("DamageReason")) BETWEEN 8 AND 500
                        AND length(btrim("DamageEvidenceReference")) BETWEEN 8 AND 500)
                ),
                CONSTRAINT "CK_BM_DepositDecision_Proof" CHECK (
                    length(btrim("ExplicitDepositPolicyReference")) BETWEEN 8 AND 200
                    AND "ApprovalSha256" ~ '^[0-9a-f]{64}$'
                    AND length("ApprovalJson") BETWEEN 10 AND 1000000
                    AND jsonb_typeof("ApprovalJson"::jsonb) = 'object'
                )
            );
            CREATE UNIQUE INDEX "UX_BM_DepositDecision_Contract"
                ON "BuildingManagementContractDepositSettlementDecisions"("ContractId");
            CREATE UNIQUE INDEX "UX_BM_DepositDecision_MoveOut"
                ON "BuildingManagementContractDepositSettlementDecisions"("MoveOutRequestId");
            CREATE UNIQUE INDEX "UX_BM_DepositDecision_FinalInvoice"
                ON "BuildingManagementContractDepositSettlementDecisions"("FinalIssuedInvoiceId");

            CREATE FUNCTION "BM_GuardDepositSettlementDecision"()
            RETURNS trigger LANGUAGE plpgsql AS $decision$
            DECLARE held numeric(18,0);
            DECLARE contract_status integer;
            DECLARE request record;
            DECLARE issued record;
            DECLARE already_outgoing numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Approved deposit settlement decision is immutable'
                        USING ERRCODE = '23514';
                END IF;
                SELECT "DepositAmount","Status" INTO held,contract_status
                  FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."ContractId" FOR UPDATE;
                SELECT * INTO request FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = NEW."MoveOutRequestId" FOR SHARE;
                SELECT * INTO issued FROM "BuildingManagementIssuedInvoiceLedger"
                 WHERE "Id" = NEW."FinalIssuedInvoiceId" FOR SHARE;
                IF held IS DISTINCT FROM NEW."VerifiedHeldAmount"
                   OR contract_status IS DISTINCT FROM 2
                   OR request."Id" IS NULL OR request."Status" <> 1
                   OR request."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR issued."Id" IS NULL
                   OR issued."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR issued."RoomId" IS DISTINCT FROM request."RoomId"
                   OR issued."MoveOutRequestId" IS DISTINCT FROM request."Id"
                   OR issued."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR NEW."ApprovedAtUtc" < issued."IssuedAtUtc" THEN
                    RAISE EXCEPTION 'Deposit decision is not backed by active contract and approved final invoice'
                        USING ERRCODE = '23514';
                END IF;
                SELECT COALESCE(sum(s."Amount"),0) INTO already_outgoing
                  FROM "BuildingManagementDepositSettlements" s
                 WHERE s."SourceContractId" = NEW."ContractId"
                   AND s."Status" = 2
                   AND s."Type" IN (1,2,4,5,6);
                IF already_outgoing <> 0 THEN
                    RAISE EXCEPTION 'Deposit has existing outgoing disposition'
                        USING ERRCODE = '23514';
                END IF;
                -- The official issue and all manually reviewed bank
                -- receipts are checked under contract fence in application.
                -- Source table and cross-contract double-spend invariants
                -- also remain enforced by existing SQL deposit triggers.
                RETURN NEW;
            END;
            $decision$;
            CREATE TRIGGER "TR_BM_DepositDecision_Immutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositSettlementDecisions"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardDepositSettlementDecision"();

            CREATE FUNCTION "BM_NoTruncateDepositDecision"()
            RETURNS trigger LANGUAGE plpgsql AS $no_truncate$
            BEGIN
                RAISE EXCEPTION 'Deposit decision history cannot be truncated'
                  USING ERRCODE = '23514';
            END;
            $no_truncate$;
            CREATE TRIGGER "TR_BM_DepositDecision_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementContractDepositSettlementDecisions"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateDepositDecision"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositSettlementDecisions"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositSettlementDecisions"
                   LIMIT 1) THEN
                    RAISE EXCEPTION 'Audited deposit decision exists; refuse rollback'
                       USING ERRCODE = '23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_DepositDecision_NoTruncate"
              ON "BuildingManagementContractDepositSettlementDecisions";
            DROP TRIGGER "TR_BM_DepositDecision_Immutable"
              ON "BuildingManagementContractDepositSettlementDecisions";
            DROP FUNCTION "BM_NoTruncateDepositDecision"();
            DROP FUNCTION "BM_GuardDepositSettlementDecision"();
            DROP TABLE "BuildingManagementContractDepositSettlementDecisions";
            """);
    }
}
