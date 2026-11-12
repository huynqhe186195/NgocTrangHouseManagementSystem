using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Completes the OI-04 accounting equation using the ORIGINAL DepositSettlement
/// journal. Previously posted debt credit cannot also be refunded. This
/// migration never moves funds or touches production data automatically.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261101026000_Phase2G4PostOffsetFinalDisposition")]
public sealed class Phase2G4PostOffsetFinalDisposition : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            ALTER TABLE "BuildingManagementContractDepositSettlementDecisions"
              DROP CONSTRAINT "CK_BM_DepositDecision_Amounts";
            ALTER TABLE "BuildingManagementContractDepositSettlementDecisions"
              ADD CONSTRAINT "CK_BM_DepositDecision_Amounts"
                CHECK ("VerifiedHeldAmount" > 0
                  AND "ApprovedForfeitAmount" >= 0
                  AND "ApprovedRefundAmount" >= 0
                  AND "ApprovedDebtOffsetAmount" >= 0
                  AND "VerifiedHeldAmount" =
                      "ApprovedForfeitAmount" + "ApprovedRefundAmount"
                      + "ApprovedDebtOffsetAmount");

            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              ADD COLUMN "ApprovedDebtOffsetAmount" numeric(18,0) NOT NULL DEFAULT 0;
            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              DROP CONSTRAINT "CK_BM_DepositCloseout_Amounts";
            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              ADD CONSTRAINT "CK_BM_DepositCloseout_Amounts"
                CHECK ("VerifiedHeldAmount" > 0
                  AND "ApprovedForfeitAmount" >= 0
                  AND "ApprovedRefundAmount" >= 0
                  AND "ApprovedDebtOffsetAmount" >= 0
                  AND "VerifiedHeldAmount" =
                      "ApprovedForfeitAmount" + "ApprovedRefundAmount"
                      + "ApprovedDebtOffsetAmount");

            CREATE OR REPLACE FUNCTION "BM_GuardDepositSettlementDecision"()
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
                IF NEW."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                     COALESCE((SELECT o."DebitHeldDepositVnd"
                       FROM "BuildingManagementContractDepositDebtOffsetPostings" o
                      WHERE o."ContractId"=NEW."ContractId"
                        AND o."MoveOutRequestId"=NEW."MoveOutRequestId"),0)
                   OR held IS DISTINCT FROM NEW."VerifiedHeldAmount"
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
                         ((SELECT COALESCE(sum(r."Amount"),0)
                            FROM "BuildingManagementOfficialInvoiceReceiptReviews" r
                           WHERE r."ReceivablePostingId" = p."Id"
                             AND r."Accepted" = TRUE)
                          + "BM_PostOffsetAppliedToInvoice"(
                                NEW."ContractId",p."Id"))
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

            CREATE OR REPLACE FUNCTION "BM_RequireApprovedDepositDecisionAtCloseout"()
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
                   approved."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                       NEW."ApprovedDebtOffsetAmount" OR
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

            CREATE OR REPLACE FUNCTION "BM_GuardDepositCloseoutApproval"()
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
                IF NEW."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                     COALESCE((SELECT o."DebitHeldDepositVnd"
                       FROM "BuildingManagementContractDepositDebtOffsetPostings" o
                      WHERE o."ContractId"=NEW."ContractId"
                        AND o."MoveOutRequestId"=NEW."MoveOutRequestId"),0)
                   OR received <> NEW."VerifiedHeldAmount" OR
                   completed_outgoing + NEW."ApprovedDebtOffsetAmount" <>
                       NEW."VerifiedHeldAmount" OR
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

            -- The original source-freeze trigger is purposefully replaced
            -- ONLY for audited post-offset refund/forfeit INSERTs. This does
            -- not reopen original deposit receipts or historical mutations.
            DROP TRIGGER "TR_BM_OI04FreezeLegacyDepositDecision"
                ON "BuildingManagementContractDepositSettlementDecisions";
            DROP TRIGGER "TR_BM_OI04FreezeLegacyCloseoutApproval"
                ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP TRIGGER "TR_BM_OI04FreezeOffsetSettlements"
                ON "BuildingManagementDepositSettlements";

            CREATE FUNCTION "BM_GuardPostOffsetOutgoingDisposition"()
            RETURNS trigger LANGUAGE plpgsql AS $post_out$
            DECLARE contract_id uuid;
            DECLARE posted record;
            DECLARE approved record;
            BEGIN
                contract_id := COALESCE(
                    NULLIF(to_jsonb(NEW)->>'SourceContractId','')::uuid,
                    NULLIF(to_jsonb(NEW)->>'TargetContractId','')::uuid,
                    NULLIF(to_jsonb(OLD)->>'SourceContractId','')::uuid,
                    NULLIF(to_jsonb(OLD)->>'TargetContractId','')::uuid);
                SELECT * INTO posted
                  FROM "BuildingManagementContractDepositDebtOffsetPostings"
                 WHERE "ContractId"=contract_id;
                IF posted."Id" IS NULL THEN
                    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END IF;
                IF TG_OP<>'INSERT' OR
                   current_setting('transaction_isolation')<>'serializable' OR
                   NEW."SourceContractId" IS DISTINCT FROM contract_id OR
                   NEW."TargetContractId" IS NOT NULL OR
                   NEW."Type" NOT IN (1,2) OR NEW."Status"<>2 OR
                   NEW."CompletedAt" IS NULL THEN
                    RAISE EXCEPTION 'Posted offset freezes all sources except authorized completed outgoing settlement'
                      USING ERRCODE='23514';
                END IF;
                -- Serialize competing direct-SQL refund attempts with
                -- the same contract lock as the authenticated writer.
                PERFORM 1 FROM "BuildingManagementContracts"
                 WHERE "Id"=contract_id FOR UPDATE;
                IF NOT FOUND OR EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                     WHERE s."SourceContractId"=contract_id
                       AND s."Type"=NEW."Type"
                       AND s."Status"=2 AND s."Id"<>NEW."Id")
                THEN
                    RAISE EXCEPTION 'Duplicate or orphaned post-offset disposition'
                      USING ERRCODE='23505';
                END IF;
                SELECT * INTO approved
                  FROM "BuildingManagementContractDepositSettlementDecisions"
                 WHERE "ContractId"=contract_id FOR SHARE;
                IF approved."Id" IS NULL OR
                   approved."MoveOutRequestId" IS DISTINCT FROM
                       posted."MoveOutRequestId" OR
                   approved."ApprovedDebtOffsetAmount" IS DISTINCT FROM
                       posted."DebitHeldDepositVnd" OR
                   NEW."CompletedAt" < approved."ApprovedAtUtc" OR
                   (NEW."Type"=1 AND
                    NEW."Amount" IS DISTINCT FROM approved."ApprovedRefundAmount") OR
                   (NEW."Type"=2 AND
                    NEW."Amount" IS DISTINCT FROM approved."ApprovedForfeitAmount")
                THEN
                    RAISE EXCEPTION 'Completed outgoing settlement lacks matching signed net-of-offset decision'
                      USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END;
            $post_out$;
            CREATE TRIGGER "TR_BM_OI04SignedPostOffsetOutgoing"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementDepositSettlements"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardPostOffsetOutgoingDisposition"();
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositDebtOffsetPostings"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementContractDepositDebtOffsetPostings" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Refuse to downgrade a posted debt offset and its settled cash history'
                      USING ERRCODE='23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_OI04SignedPostOffsetOutgoing"
              ON "BuildingManagementDepositSettlements";
            DROP FUNCTION "BM_GuardPostOffsetOutgoingDisposition"();

            CREATE TRIGGER "TR_BM_OI04FreezeLegacyDepositDecision"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositSettlementDecisions"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeLegacyCloseoutApproval"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementContractDepositCloseoutApprovals"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();
            CREATE TRIGGER "TR_BM_OI04FreezeOffsetSettlements"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementDepositSettlements"
              FOR EACH ROW EXECUTE FUNCTION "BM_BlockPostedOffsetSourceMutation"();

            ALTER TABLE "BuildingManagementContractDepositSettlementDecisions"
              DROP CONSTRAINT "CK_BM_DepositDecision_Amounts";
            ALTER TABLE "BuildingManagementContractDepositSettlementDecisions"
              ADD CONSTRAINT "CK_BM_DepositDecision_Amounts"
              CHECK ("VerifiedHeldAmount" > 0
                 AND "ApprovedForfeitAmount" >= 0
                 AND "ApprovedRefundAmount" >= 0
                 AND "ApprovedDebtOffsetAmount" = 0
                 AND "VerifiedHeldAmount" =
                     "ApprovedForfeitAmount" + "ApprovedRefundAmount");
            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              DROP CONSTRAINT "CK_BM_DepositCloseout_Amounts";
            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              ADD CONSTRAINT "CK_BM_DepositCloseout_Amounts"
              CHECK ("VerifiedHeldAmount" > 0
                 AND "ApprovedForfeitAmount" >= 0
                 AND "ApprovedRefundAmount" >= 0
                 AND "VerifiedHeldAmount" =
                     "ApprovedForfeitAmount" + "ApprovedRefundAmount");
            ALTER TABLE "BuildingManagementContractDepositCloseoutApprovals"
              DROP COLUMN "ApprovedDebtOffsetAmount";

            -- Restore pre-offset source-only functions before returning
            -- to the earlier schema. No positive-offset history can be
            -- downgraded, and zero-offset legacy behavior remains intact.
            CREATE OR REPLACE FUNCTION "BM_GuardDepositSettlementDecision"()
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

            CREATE OR REPLACE FUNCTION "BM_RequireApprovedDepositDecisionAtCloseout"()
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

            CREATE OR REPLACE FUNCTION "BM_GuardDepositCloseoutApproval"()
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
        """);
    }
}
