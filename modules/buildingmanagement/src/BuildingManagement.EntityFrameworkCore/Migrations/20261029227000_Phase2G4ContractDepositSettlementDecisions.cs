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
                -- Reject stale-snapshot direct SQL approvals. One
                -- approval must see a serializable, complete debt ledger.
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Deposit decision requires SERIALIZABLE'
                      USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                     WHERE i."ContractId" = NEW."ContractId" AND NOT EXISTS (
                         SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                          WHERE p."Kind" = 2 AND p."IssuedInvoiceId" = i."Id"
                            AND p."ContractId" = NEW."ContractId")
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                     WHERE i."ContractId" = NEW."ContractId" AND NOT EXISTS (
                         SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                          WHERE p."Kind" = 1 AND p."IssuedInvoiceId" = i."Id"
                            AND p."ContractId" = NEW."ContractId")
                ) THEN
                    RAISE EXCEPTION 'Every issued invoice must be officially posted'
                      USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" p
                     WHERE p."ContractId" = NEW."ContractId"
                       AND (
                         (SELECT count(*) FROM
                           "BuildingManagementOfficialInvoiceReceiptReviews" r
                          WHERE r."ReceivablePostingId" = p."Id"
                            AND r."Accepted" = TRUE)
                         <>
                         CASE WHEN p."Kind" = 1 THEN
                           (SELECT count(*) FROM
                             "BuildingManagementMonthlyInvoicePaymentAllocation" a
                            WHERE a."IssuedInvoiceId" = p."IssuedInvoiceId")
                         ELSE
                           (SELECT count(*) FROM
                             "BuildingManagementInvoicePaymentAllocationJournal" a
                            WHERE a."IssuedInvoiceId" = p."IssuedInvoiceId")
                         END
                         OR
                         p."OriginalAmount" +
                           CASE WHEN p."Kind" = 1 THEN
                             (SELECT COALESCE(sum(d."ApprovedSignedDeltaAmount"),0)
                                FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions" d
                               WHERE d."IssuedInvoiceId" = p."IssuedInvoiceId"
                                 AND d."Accepted" = TRUE)
                           ELSE 0 END
                         <>
                         (SELECT COALESCE(sum(r."Amount"),0)
                            FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
                           WHERE r."ReceivablePostingId" = p."Id"
                             AND r."Accepted" = TRUE)
                         OR EXISTS (
                             SELECT 1 FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
                              WHERE r."ReceivablePostingId" = p."Id"
                                AND r."ReviewedAtUtc" > NEW."ApprovedAtUtc"
                         )
                       )
                ) THEN
                    RAISE EXCEPTION
                      'Deposit decision requires every official receivable fully independently reconciled'
                      USING ERRCODE = '23514';
                END IF;
                -- Source amount/provenance and cross-contract double-spend
                -- checks remain enforced by existing financial triggers.
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
        builder.Sql("""
            -- A direct SQL caller must not bypass the separately approved
            -- 2G.4 decision by inserting only a legacy 2G.5 closeout row.
            CREATE FUNCTION "BM_RequireApprovedDepositDecisionAtCloseout"()
            RETURNS trigger LANGUAGE plpgsql AS $closeout_decision$
            DECLARE approved record;
            BEGIN
                SELECT * INTO approved
                  FROM "BuildingManagementContractDepositSettlementDecisions"
                 WHERE "ContractId" = NEW."ContractId"
                   AND "MoveOutRequestId" = NEW."MoveOutRequestId"
                 FOR SHARE;
                IF approved."Id" IS NULL OR
                   approved."FinalIssuedInvoiceId" IS DISTINCT FROM
                       NEW."IssuedInvoiceId" OR
                   approved."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                   approved."VerifiedHeldAmount" IS DISTINCT FROM
                       NEW."VerifiedHeldAmount" OR
                   approved."ApprovedForfeitAmount" IS DISTINCT FROM
                       NEW."ApprovedForfeitAmount" OR
                   approved."ApprovedRefundAmount" IS DISTINCT FROM
                       NEW."ApprovedRefundAmount" OR
                   approved."ApprovedDebtOffsetAmount" <> 0 OR
                   approved."DamageReason" IS DISTINCT FROM NEW."DamageReason" OR
                   approved."DamageEvidenceReference" IS DISTINCT FROM
                       NEW."DamageEvidenceReference" OR
                   approved."ExplicitDepositPolicyReference" IS DISTINCT FROM
                       NEW."ExplicitDepositPolicyReference" OR
                   approved."ApprovedAtUtc" > NEW."ApprovedAtUtc" THEN
                    RAISE EXCEPTION
                      'Closeout must match previously approved deposit settlement decision'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $closeout_decision$;
            CREATE TRIGGER "TR_BM_00_RequireApprovedDepositDecision"
              BEFORE INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
              FOR EACH ROW
              EXECUTE FUNCTION "BM_RequireApprovedDepositDecisionAtCloseout"();
            """);
        builder.Sql("""
            -- Reuse the original DepositSettlement journal: an outgoing
            -- contract refund or forfeiture must be PRE-authorized by the
            -- immutable 2G.4 decision, not by a new parallel money ledger.
            CREATE FUNCTION "BM_RequireDepositDecisionForOutgoing"()
            RETURNS trigger LANGUAGE plpgsql AS $outgoing$
            DECLARE approved record;
            DECLARE earlier bigint;
            BEGIN
                IF NEW."SourceContractId" IS NULL OR
                   NEW."Type" NOT IN (1, 2) THEN
                    RETURN NEW;
                END IF;
                SELECT * INTO approved
                  FROM "BuildingManagementContractDepositSettlementDecisions"
                 WHERE "ContractId" = NEW."SourceContractId"
                 FOR SHARE;
                IF approved."Id" IS NULL OR
                   (NEW."Type" = 1 AND
                     (approved."ApprovedRefundAmount" <= 0 OR
                      NEW."Amount" IS DISTINCT FROM approved."ApprovedRefundAmount")) OR
                   (NEW."Type" = 2 AND
                     (approved."ApprovedForfeitAmount" <= 0 OR
                      NEW."Amount" IS DISTINCT FROM approved."ApprovedForfeitAmount")) THEN
                    RAISE EXCEPTION
                      'Contract deposit refund/forfeit requires matching approved decision'
                      USING ERRCODE = '23514';
                END IF;
                IF NEW."Status" = 2 THEN
                    IF NEW."CompletedAt" IS NULL OR
                       NEW."CompletedAt" < approved."ApprovedAtUtc" OR
                       (NEW."Type" = 1 AND
                        (NEW."Notes" IS NULL OR
                         NEW."Notes" NOT LIKE 'MANUAL BANK REF %')) OR
                       (NEW."Type" = 2 AND
                        (NEW."Notes" IS NULL OR
                         position(approved."DamageReason" IN NEW."Notes") = 0)) THEN
                        RAISE EXCEPTION
                          'Completed contract deposit disposition lacks approved evidence'
                          USING ERRCODE = '23514';
                    END IF;
                    SELECT count(*) INTO earlier
                      FROM "BuildingManagementDepositSettlements" s
                     WHERE s."SourceContractId" = NEW."SourceContractId"
                       AND s."Type" = NEW."Type" AND s."Status" = 2
                       AND s."Id" <> NEW."Id";
                    IF earlier <> 0 THEN
                        RAISE EXCEPTION 'Contract deposit already disposed for this kind'
                          USING ERRCODE = '23505';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $outgoing$;
            CREATE TRIGGER "TR_BM_00_RequireDepositDecisionForOutgoing"
              BEFORE INSERT OR UPDATE
              ON "BuildingManagementDepositSettlements"
              FOR EACH ROW
              EXECUTE FUNCTION "BM_RequireDepositDecisionForOutgoing"();
            """);
        builder.Sql("""
            -- Freeze the fully reconciled invoice graph once the owner
            -- signs the deposit plan. New billing/credit notes must not
            -- silently invalidate that immutable closeout decision.
            CREATE FUNCTION "BM_BlockNewFinancialEventsAfterDepositDecision"()
            RETURNS trigger LANGUAGE plpgsql AS $after_decision$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositSettlementDecisions" d
                     WHERE d."ContractId" = NEW."ContractId"
                ) THEN
                    RAISE EXCEPTION
                      'Financial issuance and adjustment cannot follow deposit decision'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $after_decision$;
            CREATE TRIGGER "TR_BM_00_NoFinalIssueAfterDepositDecision"
              BEFORE INSERT ON "BuildingManagementIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockNewFinancialEventsAfterDepositDecision"();
            CREATE TRIGGER "TR_BM_00_NoMonthlyIssueAfterDepositDecision"
              BEFORE INSERT ON "BuildingManagementMonthlyIssuedInvoiceLedger"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockNewFinancialEventsAfterDepositDecision"();
            CREATE TRIGGER "TR_BM_00_NoAdjustmentAfterDepositDecision"
              BEFORE INSERT ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockNewFinancialEventsAfterDepositDecision"();
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
            DROP TRIGGER "TR_BM_00_NoAdjustmentAfterDepositDecision"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            DROP TRIGGER "TR_BM_00_NoMonthlyIssueAfterDepositDecision"
              ON "BuildingManagementMonthlyIssuedInvoiceLedger";
            DROP TRIGGER "TR_BM_00_NoFinalIssueAfterDepositDecision"
              ON "BuildingManagementIssuedInvoiceLedger";
            DROP FUNCTION "BM_BlockNewFinancialEventsAfterDepositDecision"();
            DROP TRIGGER "TR_BM_00_RequireDepositDecisionForOutgoing"
              ON "BuildingManagementDepositSettlements";
            DROP FUNCTION "BM_RequireDepositDecisionForOutgoing"();
            DROP TRIGGER "TR_BM_00_RequireApprovedDepositDecision"
              ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP FUNCTION "BM_RequireApprovedDepositDecisionAtCloseout"();
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
