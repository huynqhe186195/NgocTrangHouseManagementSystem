using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// SOURCE-ONLY, additive final deposit approval and completed settlement
/// evidence. It never triggers an external refund or lets an invoice
/// journal automatically use a deposit for debt reduction.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029204000_SliceBContractDepositCloseoutApprovals")]
public sealed class SliceBContractDepositCloseoutApprovals : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementContractDepositCloseoutApprovals" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ContractId" uuid NOT NULL,
                "MoveOutRequestId" uuid NOT NULL,
                "IssuedInvoiceId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "ApprovedByUserId" uuid NOT NULL,
                "ApprovedAtUtc" timestamptz NOT NULL,
                "VerifiedHeldAmount" numeric(18,0) NOT NULL,
                "ApprovedForfeitAmount" numeric(18,0) NOT NULL,
                "ApprovedRefundAmount" numeric(18,0) NOT NULL,
                "RefundSettlementId" uuid NULL,
                "ForfeitSettlementId" uuid NULL,
                "RefundTransactionReference" varchar(128) NULL,
                "RefundPaidAtUtc" timestamptz NULL,
                "DamageReason" varchar(500) NULL,
                "DamageEvidenceReference" varchar(500) NULL,
                "InspectionEvidenceReference" varchar(500) NOT NULL,
                "HandoverEvidenceReference" varchar(500) NOT NULL,
                "ExplicitDepositPolicyReference" varchar(200) NOT NULL,
                "ApprovalJson" text NOT NULL,
                "ApprovalSha256" varchar(64) NOT NULL,
                CONSTRAINT "FK_BM_DepositCloseout_Contract"
                    FOREIGN KEY ("ContractId")
                    REFERENCES "BuildingManagementContracts" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_DepositCloseout_Request"
                    FOREIGN KEY ("MoveOutRequestId")
                    REFERENCES "BuildingManagementMoveOutRequests" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_DepositCloseout_IssuedInvoice"
                    FOREIGN KEY ("IssuedInvoiceId")
                    REFERENCES "BuildingManagementIssuedInvoiceLedger" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_DepositCloseout_RefundSettlement"
                    FOREIGN KEY ("RefundSettlementId")
                    REFERENCES "BuildingManagementDepositSettlements" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_DepositCloseout_ForfeitSettlement"
                    FOREIGN KEY ("ForfeitSettlementId")
                    REFERENCES "BuildingManagementDepositSettlements" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_DepositCloseout_Amounts"
                    CHECK ("VerifiedHeldAmount" > 0
                       AND "ApprovedForfeitAmount" >= 0
                       AND "ApprovedRefundAmount" >= 0
                       AND "VerifiedHeldAmount" =
                           "ApprovedRefundAmount" + "ApprovedForfeitAmount"),
                CONSTRAINT "CK_BM_DepositCloseout_Docs"
                    CHECK (length(trim("InspectionEvidenceReference")) BETWEEN 8 AND 500
                       AND length(trim("HandoverEvidenceReference")) BETWEEN 8 AND 500
                       AND length(trim("ExplicitDepositPolicyReference")) BETWEEN 8 AND 200),
                CONSTRAINT "CK_BM_DepositCloseout_Refund"
                    CHECK (("ApprovedRefundAmount" = 0
                            AND "RefundSettlementId" IS NULL
                            AND "RefundTransactionReference" IS NULL
                            AND "RefundPaidAtUtc" IS NULL)
                        OR ("ApprovedRefundAmount" > 0
                            AND "RefundSettlementId" IS NOT NULL
                            AND length(trim("RefundTransactionReference")) BETWEEN 8 AND 128
                            AND "RefundPaidAtUtc" IS NOT NULL)),
                CONSTRAINT "CK_BM_DepositCloseout_Forfeit"
                    CHECK (("ApprovedForfeitAmount" = 0
                            AND "ForfeitSettlementId" IS NULL
                            AND "DamageReason" IS NULL
                            AND "DamageEvidenceReference" IS NULL)
                        OR ("ApprovedForfeitAmount" > 0
                            AND "ForfeitSettlementId" IS NOT NULL
                            AND length(trim("DamageReason")) BETWEEN 8 AND 500
                            AND length(trim("DamageEvidenceReference")) BETWEEN 8 AND 500)),
                CONSTRAINT "CK_BM_DepositCloseout_ProofHash"
                    CHECK ("ApprovalSha256" ~ '^[0-9a-f]{64}$'
                       AND length("ApprovalJson") BETWEEN 1 AND 1000000
                       AND jsonb_typeof("ApprovalJson"::jsonb) = 'object'),
                CONSTRAINT "CK_BM_DepositCloseout_Ids"
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ContractId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "MoveOutRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "IssuedInvoiceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ApprovedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid)
            );
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_Contract"
                ON "BuildingManagementContractDepositCloseoutApprovals" ("ContractId");
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_Request"
                ON "BuildingManagementContractDepositCloseoutApprovals" ("MoveOutRequestId");
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_IssuedInvoice"
                ON "BuildingManagementContractDepositCloseoutApprovals" ("IssuedInvoiceId");
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_Refund"
                ON "BuildingManagementContractDepositCloseoutApprovals"
                ("RefundSettlementId") WHERE "RefundSettlementId" IS NOT NULL;
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_Forfeit"
                ON "BuildingManagementContractDepositCloseoutApprovals"
                ("ForfeitSettlementId") WHERE "ForfeitSettlementId" IS NOT NULL;

            CREATE FUNCTION "BM_GuardDepositCloseoutApproval"()
            RETURNS trigger LANGUAGE plpgsql AS $deposit_guard$
            DECLARE invoice record;
            DECLARE request record;
            DECLARE refund record;
            DECLARE forfeiture record;
            DECLARE received numeric(18,0);
            DECLARE completed_outgoing numeric(18,0);
            DECLARE contract_held numeric(18,0);
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Approved deposit closeout history is immutable'
                        USING ERRCODE = '23514';
                END IF;
                -- Serialize approval with every in-scope deposit mutation.
                -- The staging writer already takes this contract lock;
                -- the database repeats it to defend direct SQL callers.
                SELECT "DepositAmount" INTO contract_held
                  FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."ContractId" FOR UPDATE;
                IF NOT FOUND OR contract_held IS DISTINCT FROM NEW."VerifiedHeldAmount" THEN
                    RAISE EXCEPTION 'Approved closeout must equal contract held deposit'
                        USING ERRCODE = '23514';
                END IF;

                SELECT * INTO request
                  FROM "BuildingManagementMoveOutRequests"
                 WHERE "Id" = NEW."MoveOutRequestId" FOR SHARE;
                SELECT * INTO invoice
                  FROM "BuildingManagementIssuedInvoiceLedger"
                 WHERE "Id" = NEW."IssuedInvoiceId" FOR SHARE;
                IF request."Id" IS NULL OR invoice."Id" IS NULL OR
                   request."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                   invoice."ContractId" IS DISTINCT FROM NEW."ContractId" OR
                   invoice."RoomId" IS DISTINCT FROM request."RoomId" OR
                   invoice."MoveOutRequestId" IS DISTINCT FROM request."Id" OR
                   invoice."BuildingId" IS DISTINCT FROM NEW."BuildingId" OR
                   request."Status" <> 1 OR
                   NEW."ApprovedAtUtc" < invoice."IssuedAtUtc" THEN
                    RAISE EXCEPTION 'Deposit closeout must match approved contract move-out and issued invoice'
                        USING ERRCODE = '23514';
                END IF;

                -- The selected Refund/Forfeit records MUST have been
                -- completed and belong to the same locked contract.
                IF NEW."RefundSettlementId" IS NOT NULL THEN
                    SELECT * INTO refund
                      FROM "BuildingManagementDepositSettlements"
                     WHERE "Id" = NEW."RefundSettlementId" FOR SHARE;
                    IF refund."Id" IS NULL OR refund."Type" <> 1 OR
                       refund."Status" <> 2 OR
                       refund."SourceContractId" IS DISTINCT FROM NEW."ContractId" OR
                       refund."Amount" IS DISTINCT FROM NEW."ApprovedRefundAmount" OR
                       refund."CompletedAt" IS DISTINCT FROM NEW."RefundPaidAtUtc" OR
                       refund."Notes" IS NULL OR
                       position(NEW."RefundTransactionReference" IN refund."Notes") = 0 THEN
                        RAISE EXCEPTION 'Approved refund lacks matching completed manual bank evidence'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                IF NEW."ForfeitSettlementId" IS NOT NULL THEN
                    SELECT * INTO forfeiture
                      FROM "BuildingManagementDepositSettlements"
                     WHERE "Id" = NEW."ForfeitSettlementId" FOR SHARE;
                    IF forfeiture."Id" IS NULL OR forfeiture."Type" <> 2 OR
                       forfeiture."Status" <> 2 OR
                       forfeiture."SourceContractId" IS DISTINCT FROM NEW."ContractId" OR
                       forfeiture."Amount" IS DISTINCT FROM NEW."ApprovedForfeitAmount" OR
                       forfeiture."Notes" IS NULL OR
                       position(NEW."DamageReason" IN forfeiture."Notes") = 0 THEN
                        RAISE EXCEPTION 'Approved damage deduction lacks matching documented forfeiture'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;

                -- A closed contract must have EVERY issued invoice fully
                -- matched, not only its final move-out invoice. Enforce the
                -- hard debt-clearance boundary in SQL as well as the app.
                -- No missing invoice, partially paid invoice, or unsupported
                -- excess collection can be approved via direct SQL.
                IF NOT EXISTS (
                    SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger"
                     WHERE "Id" = NEW."IssuedInvoiceId"
                       AND "ContractId" = NEW."ContractId"
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                    LEFT JOIN "BuildingManagementInvoicePaymentAllocationJournal" a
                      ON a."IssuedInvoiceId" = i."Id"
                    WHERE i."ContractId" = NEW."ContractId"
                    GROUP BY i."Id", i."TotalAmount"
                    HAVING COALESCE(sum(a."Amount"),0) <>
                           i."TotalAmount"
                ) THEN
                    RAISE EXCEPTION 'Every issued invoice requires complete audited allocation'
                        USING ERRCODE = '23514';
                END IF;

                -- Allocation arithmetic alone is insufficient: a previously
                -- completed receipt could have been directly altered or
                -- deleted after its staging journal was created. Re-attest
                -- every paid invoice journal against its current source.
                IF EXISTS (
                    SELECT 1
                    FROM "BuildingManagementInvoicePaymentAllocationJournal" a
                    LEFT JOIN "BuildingManagementPayments" p
                      ON p."Id" = a."PaymentId"
                    WHERE a."ContractId" = NEW."ContractId"
                      AND (p."Id" IS NULL
                           OR p."Purpose" IS DISTINCT FROM 3
                           OR p."Status" IS DISTINCT FROM 2
                           OR p."IsDeleted" IS DISTINCT FROM FALSE
                           OR p."InvoiceId" IS DISTINCT FROM a."IssuedInvoiceId"
                           OR p."ContractId" IS DISTINCT FROM a."ContractId"
                           OR p."Amount" IS DISTINCT FROM a."Amount")
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments" p
                    WHERE p."ContractId" = NEW."ContractId"
                      AND p."Purpose" = 3 AND p."Status" = 2
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "BuildingManagementInvoicePaymentAllocationJournal" a
                          WHERE a."PaymentId" = p."Id"
                      )
                ) THEN
                    RAISE EXCEPTION 'Invoice receipt source drift or unjournaled completed payment'
                        USING ERRCODE = '23514';
                END IF;

                SELECT COALESCE(sum("Amount"),0) INTO received
                  FROM "BuildingManagementDepositSettlements"
                 WHERE "TargetContractId" = NEW."ContractId"
                   AND "Type" = 3 AND "Status" = 2;
                SELECT COALESCE(sum("Amount"),0) INTO completed_outgoing
                  FROM "BuildingManagementDepositSettlements"
                 WHERE "SourceContractId" = NEW."ContractId"
                   AND "Type" IN (1,2,4,5,6) AND "Status" = 2;
                IF received <> NEW."VerifiedHeldAmount" OR
                   completed_outgoing <> NEW."VerifiedHeldAmount" OR
                   EXISTS (
                       SELECT 1 FROM "BuildingManagementDepositSettlements"
                        WHERE ("SourceContractId" = NEW."ContractId"
                               OR "TargetContractId" = NEW."ContractId")
                          AND "Status" = 1
                   ) THEN
                    RAISE EXCEPTION 'Deposit closeout must fully reconcile completed allocations and disbursements'
                        USING ERRCODE = '23514';
                END IF;

                RETURN NEW;
            END;
            $deposit_guard$;
            CREATE TRIGGER "TR_BM_DepositCloseout_Immutable"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementContractDepositCloseoutApprovals"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardDepositCloseoutApproval"();
            CREATE TRIGGER "TR_BM_DepositCloseout_Truncate"
                BEFORE TRUNCATE ON "BuildingManagementContractDepositCloseoutApprovals"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardDepositCloseoutApproval"();

            -- No later incoming deposit allocation, additional forfeiture,
            -- replacement refund or mutation can rewrite an approved plan.
            CREATE FUNCTION "BM_GuardFinalizedDepositSettlements"()
            RETURNS trigger LANGUAGE plpgsql AS $settlement_guard$
            DECLARE affected_contract uuid;
            BEGIN
                affected_contract := CASE WHEN TG_OP = 'DELETE'
                    THEN COALESCE(OLD."SourceContractId", OLD."TargetContractId")
                    ELSE COALESCE(NEW."SourceContractId", NEW."TargetContractId")
                END;
                IF affected_contract IS NOT NULL THEN
                    PERFORM 1 FROM "BuildingManagementContracts"
                    WHERE "Id" = affected_contract FOR UPDATE;
                END IF;
                IF (TG_OP IN ('UPDATE','DELETE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = OLD."SourceContractId"
                        OR "ContractId" = OLD."TargetContractId"
                        OR "RefundSettlementId" = OLD."Id"
                        OR "ForfeitSettlementId" = OLD."Id"
                )) OR
                   (TG_OP IN ('INSERT','UPDATE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = NEW."SourceContractId"
                        OR "ContractId" = NEW."TargetContractId"
                )) THEN
                    RAISE EXCEPTION 'Approved contract deposit settlement history cannot be modified'
                        USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $settlement_guard$;
            CREATE TRIGGER "TR_BM_DepositCloseout_ProtectSettlements"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementDepositSettlements"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardFinalizedDepositSettlements"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals",
                       "BuildingManagementDepositSettlements"
                IN ACCESS EXCLUSIVE MODE;
            DO $closeout_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1) THEN
                    RAISE EXCEPTION 'Approved deposit closeout history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $closeout_down$;
            DROP TRIGGER "TR_BM_DepositCloseout_ProtectSettlements"
                ON "BuildingManagementDepositSettlements";
            DROP TABLE "BuildingManagementContractDepositCloseoutApprovals";
            DROP FUNCTION "BM_GuardFinalizedDepositSettlements"();
            DROP FUNCTION "BM_GuardDepositCloseoutApproval"();
            """);
    }
}
