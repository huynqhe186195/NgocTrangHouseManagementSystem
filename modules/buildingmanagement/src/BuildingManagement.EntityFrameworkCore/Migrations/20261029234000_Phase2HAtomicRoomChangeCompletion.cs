using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2H one-shot atomic event. This adds a deferred commit gate that
/// rejects any contract/room/deposit transfer without a whole-transaction
/// completion record, approved choice, matching roster and settled ledger.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029234000_Phase2HAtomicRoomChangeCompletion")]
public sealed class Phase2HAtomicRoomChangeCompletion : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            ALTER TABLE "BuildingManagementRoomChangeRequests"
              DROP CONSTRAINT "CK_BM_RoomChangeRequest_Valid";
            ALTER TABLE "BuildingManagementRoomChangeRequests"
              ADD CONSTRAINT "CK_BM_RoomChangeRequest_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "SourceRoomId" <> "TargetRoomId"
                    AND "Status" IN (0,1,2,3,4)
                    AND "RequestedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND length(btrim("Reason")) BETWEEN 8 AND 500
                    AND (("Status" = 0 AND "ReviewedAtUtc" IS NULL
                          AND "ReviewedByUserId" IS NULL AND "ReviewNotes" IS NULL)
                      OR ("Status" IN (1,2,3,4)
                          AND "ReviewedByUserId" IS NOT NULL
                          AND "ReviewedAtUtc" >= "CreatedAtUtc"
                          AND length(btrim("ReviewNotes")) BETWEEN 8 AND 500))
                );
            """);
        builder.Sql("""
            CREATE OR REPLACE FUNCTION "BM_GuardRoomChangeRequest"()
                        RETURNS trigger LANGUAGE plpgsql AS $room_change$
                        DECLARE source_contract record;
                        DECLARE source_building uuid;
                        DECLARE target_building uuid;
                        DECLARE source_status integer;
                        DECLARE target_status integer;
                        BEGIN
                            IF TG_OP = 'DELETE' THEN
                                RAISE EXCEPTION 'Room change review history cannot be deleted'
                                  USING ERRCODE = '23514';
                            ELSIF TG_OP = 'UPDATE' THEN
                                IF OLD."Status" IN (2,3,4)
                                   OR NEW."Id" IS DISTINCT FROM OLD."Id"
                                   OR NEW."SourceContractId" IS DISTINCT FROM OLD."SourceContractId"
                                   OR NEW."SourceRoomId" IS DISTINCT FROM OLD."SourceRoomId"
                                   OR NEW."TargetRoomId" IS DISTINCT FROM OLD."TargetRoomId"
                                   OR NEW."BuildingId" IS DISTINCT FROM OLD."BuildingId"
                                   OR NEW."RequestedDate" IS DISTINCT FROM OLD."RequestedDate"
                                   OR NEW."Reason" IS DISTINCT FROM OLD."Reason"
                                   OR NEW."RequestedByUserId" IS DISTINCT FROM OLD."RequestedByUserId"
                                   OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                                   OR NEW."Status" NOT IN (1,2,3,4)
                                   OR (OLD."Status" = 1 AND NEW."Status" NOT IN (2,3,4))
                                   OR (OLD."Status" = 1 AND NEW."Status" = 1)
                                   OR NEW."ReviewedAtUtc" < OLD."ReviewedAtUtc" THEN
                                    RAISE EXCEPTION 'Cannot rewrite or advance unauthorized room change'
                                      USING ERRCODE = '23514';
                                END IF;
                                RETURN NEW;
                            END IF;

                            -- Intake is NOT approval. Snapshot the current source and
                            -- target, including same-building invariants, under locks.
                            IF NEW."Status" <> 0 THEN
                                RAISE EXCEPTION 'New room change must start Pending'
                                  USING ERRCODE = '23514';
                            END IF;
                            SELECT "RoomId","Status","IsDeleted" INTO source_contract
                              FROM "BuildingManagementContracts"
                             WHERE "Id" = NEW."SourceContractId" FOR SHARE;
                            SELECT r."Status", f."BuildingId"
                              INTO source_status,source_building
                              FROM "BuildingManagementRooms" r
                              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                             WHERE r."Id" = NEW."SourceRoomId"
                               AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                             FOR SHARE OF r, f;
                            SELECT r."Status", f."BuildingId"
                              INTO target_status,target_building
                              FROM "BuildingManagementRooms" r
                              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                             WHERE r."Id" = NEW."TargetRoomId"
                               AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                             FOR SHARE OF r, f;
                            IF source_contract."RoomId" IS DISTINCT FROM NEW."SourceRoomId"
                               OR source_contract."Status" IS DISTINCT FROM 2
                               OR source_contract."IsDeleted" IS DISTINCT FROM FALSE
                               OR source_status IS DISTINCT FROM 2
                               OR target_status IS DISTINCT FROM 1
                               OR source_building IS DISTINCT FROM NEW."BuildingId"
                               OR target_building IS DISTINCT FROM NEW."BuildingId"
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementMoveOutRequests" m
                                   WHERE m."ContractId" = NEW."SourceContractId"
                                     AND m."IsDeleted" = FALSE AND m."Status" IN (0,1))
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementContracts" c
                                   WHERE c."RoomId" = NEW."TargetRoomId"
                                     AND c."IsDeleted" = FALSE AND c."Status" IN (2,5))
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementRoomReservations" rr
                                   WHERE rr."RoomId" = NEW."TargetRoomId"
                                     AND rr."IsDeleted" = FALSE AND rr."Status" IN (1,2))
                            THEN
                                RAISE EXCEPTION 'Room change source or target is not eligible'
                                  USING ERRCODE = '23514';
                            END IF;
                            RETURN NEW;
                        END;
                        $room_change$;
            """);
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeCompletions" (
                "Id" uuid PRIMARY KEY,
                "RequestId" uuid NOT NULL REFERENCES
                    "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
                "SourceContractId" uuid NOT NULL REFERENCES
                    "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
                "TargetContractId" uuid NOT NULL REFERENCES
                    "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
                "SourceRoomId" uuid NOT NULL REFERENCES
                    "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
                "TargetRoomId" uuid NOT NULL REFERENCES
                    "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "PolicyId" uuid NOT NULL REFERENCES
                    "BuildingManagementRoomChangeBusinessPolicies"("Id") ON DELETE RESTRICT,
                "PolicyReviewId" uuid NOT NULL REFERENCES
                    "BuildingManagementRoomChangeBusinessPolicyReviews"("Id") ON DELETE RESTRICT,
                "RosterSnapshotId" uuid NOT NULL REFERENCES
                    "BuildingManagementRoomChangeTenantRosterSnapshots"("Id") ON DELETE RESTRICT,
                "HandoverEvidenceId" uuid NOT NULL REFERENCES
                    "BuildingManagementRoomChangeHandoverEvidence"("Id") ON DELETE RESTRICT,
                "CarryForwardSettlementId" uuid NOT NULL REFERENCES
                    "BuildingManagementDepositSettlements"("Id") ON DELETE RESTRICT,
                "CarriedDepositAmount" numeric(18,0) NOT NULL,
                "SourceRoomDisposition" integer NOT NULL,
                "SourceDispositionEvidenceReference" varchar(500) NOT NULL,
                "CompletionReference" varchar(500) NOT NULL,
                "CompletedByUserId" uuid NOT NULL,
                "CompletedAtUtc" timestamptz NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_RoomChangeCompletion_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "SourceContractId" <> "TargetContractId"
                    AND "SourceRoomId" <> "TargetRoomId"
                    AND "CarriedDepositAmount" > 0
                    AND "SourceRoomDisposition" IN (1,3,4)
                    AND length(btrim("SourceDispositionEvidenceReference")) BETWEEN 12 AND 500
                    AND length(btrim("CompletionReference")) BETWEEN 12 AND 500
                    AND "CompletedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangeComplete_Request"
              ON "BuildingManagementRoomChangeCompletions"("RequestId");
            CREATE UNIQUE INDEX "UX_BM_RoomChangeComplete_SourceContract"
              ON "BuildingManagementRoomChangeCompletions"("SourceContractId");
            CREATE UNIQUE INDEX "UX_BM_RoomChangeComplete_TargetContract"
              ON "BuildingManagementRoomChangeCompletions"("TargetContractId");
            CREATE UNIQUE INDEX "UX_BM_RoomChangeComplete_DepositDisposition"
              ON "BuildingManagementRoomChangeCompletions"("CarryForwardSettlementId");

            CREATE FUNCTION "BM_GuardRoomChangeCompletionHistory"()
            RETURNS trigger LANGUAGE plpgsql AS $history$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Completed room change history is append-only'
                      USING ERRCODE='23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'Complete room change requires SERIALIZABLE'
                      USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END;
            $history$;
            CREATE TRIGGER "TR_BM_RoomChangeCompletion_Immutable"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeCompletions"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeCompletionHistory"();

            CREATE FUNCTION "BM_NoTruncateRoomChangeCompletions"()
            RETURNS trigger LANGUAGE plpgsql AS $no_truncate$
            BEGIN
                RAISE EXCEPTION 'Completed room change history cannot be truncated'
                  USING ERRCODE='23514';
            END;
            $no_truncate$;
            CREATE TRIGGER "TR_BM_RoomChangeCompletion_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeCompletions"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangeCompletions"();

            CREATE FUNCTION "BM_VerifyRoomChangeCommit"()
            RETURNS trigger LANGUAGE plpgsql AS $completion$
            DECLARE r record;
            DECLARE p record;
            DECLARE review record;
            DECLARE roster record;
            DECLARE h record;
            DECLARE source_contract record;
            DECLARE target_contract record;
            DECLARE carry record;
            DECLARE source_status integer;
            DECLARE target_status integer;
            DECLARE other_held numeric(18,0);
            BEGIN
                SELECT * INTO r FROM "BuildingManagementRoomChangeRequests"
                 WHERE "Id"=NEW."RequestId";
                SELECT * INTO p FROM "BuildingManagementRoomChangeBusinessPolicies"
                 WHERE "Id"=NEW."PolicyId";
                SELECT * INTO review FROM "BuildingManagementRoomChangeBusinessPolicyReviews"
                 WHERE "Id"=NEW."PolicyReviewId";
                SELECT * INTO roster FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
                 WHERE "Id"=NEW."RosterSnapshotId";
                SELECT * INTO h FROM "BuildingManagementRoomChangeHandoverEvidence"
                 WHERE "Id"=NEW."HandoverEvidenceId";
                SELECT * INTO source_contract FROM "BuildingManagementContracts"
                 WHERE "Id"=NEW."SourceContractId";
                SELECT * INTO target_contract FROM "BuildingManagementContracts"
                 WHERE "Id"=NEW."TargetContractId";
                SELECT * INTO carry FROM "BuildingManagementDepositSettlements"
                 WHERE "Id"=NEW."CarryForwardSettlementId";
                SELECT "Status" INTO source_status FROM "BuildingManagementRooms"
                 WHERE "Id"=NEW."SourceRoomId";
                SELECT "Status" INTO target_status FROM "BuildingManagementRooms"
                 WHERE "Id"=NEW."TargetRoomId";
                IF r."Id" IS NULL OR r."Status" <> 4
                   OR r."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
                   OR r."SourceRoomId" IS DISTINCT FROM NEW."SourceRoomId"
                   OR r."TargetRoomId" IS DISTINCT FROM NEW."TargetRoomId"
                   OR r."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR r."ReviewNotes" IS DISTINCT FROM NEW."CompletionReference"
                   OR r."ReviewedByUserId" IS DISTINCT FROM NEW."CompletedByUserId"
                   OR r."ReviewedAtUtc" IS DISTINCT FROM NEW."CompletedAtUtc"
                   OR r."RequestedDate" <> (
                      date_trunc('month', r."RequestedDate"::timestamp)
                        + interval '1 month - 1 day')::date
                   OR r."RequestedDate" > (NEW."CompletedAtUtc" AT TIME ZONE
                        'Asia/Ho_Chi_Minh')::date
                   OR p."Id" IS NULL OR p."RequestId" IS DISTINCT FROM r."Id"
                   OR p."SourceContractId" IS DISTINCT FROM r."SourceContractId"
                   OR p."BuildingId" IS DISTINCT FROM r."BuildingId"
                   OR p."ContractTreatment" <> 1 OR p."PricingTreatment" <> 1
                   OR p."DepositTreatment" <> 1 OR p."TenantTreatment" <> 1
                   OR review."Id" IS NULL OR review."Accepted" IS DISTINCT FROM TRUE
                   OR review."PolicyId" IS DISTINCT FROM p."Id"
                   OR review."RequestId" IS DISTINCT FROM r."Id"
                   OR review."BuildingId" IS DISTINCT FROM r."BuildingId"
                   OR review."ProposalSha256" IS DISTINCT FROM p."EvidenceSha256"
                   OR review."ReviewedByUserId" = p."ProposedByUserId"
                   OR roster."Id" IS NULL OR p."RosterSnapshotId" IS DISTINCT FROM roster."Id"
                   OR p."RosterSha256" IS DISTINCT FROM roster."RosterSha256"
                   OR roster."RoomChangeRequestId" IS DISTINCT FROM r."Id"
                   OR h."Id" IS NULL OR h."RequestId" IS DISTINCT FROM r."Id"
                   OR h."SourceContractId" IS DISTINCT FROM r."SourceContractId"
                   OR h."SourceRoomId" IS DISTINCT FROM r."SourceRoomId"
                   OR h."BuildingId" IS DISTINCT FROM r."BuildingId"
                   OR h."HandoverDate" IS DISTINCT FROM r."RequestedDate"
                   OR source_contract."Id" IS NULL OR source_contract."Status" <> 3
                   OR source_contract."EndReason" <> 4
                   OR source_contract."ActualEndedAt" IS NULL
                   OR source_contract."RoomId" IS DISTINCT FROM r."SourceRoomId"
                   OR target_contract."Id" IS NULL OR target_contract."Status" <> 2
                   OR target_contract."RoomId" IS DISTINCT FROM r."TargetRoomId"
                   OR target_contract."RenewedFromContractId" IS DISTINCT FROM source_contract."Id"
                   OR target_contract."StartDate" IS DISTINCT FROM (r."RequestedDate"+1)
                   OR target_contract."MonthlyRent" <> source_contract."MonthlyRent"
                   OR target_contract."DepositAmount" <> source_contract."DepositAmount"
                   OR source_contract."DepositAmount" <= 0
                   OR source_status IS DISTINCT FROM NEW."SourceRoomDisposition"
                   OR target_status <> 2
                   OR carry."Id" IS NULL OR carry."Type" <> 5 OR carry."Status" <> 2
                   OR carry."SourceContractId" IS DISTINCT FROM source_contract."Id"
                   OR carry."TargetContractId" IS DISTINCT FROM target_contract."Id"
                   OR carry."Amount" IS DISTINCT FROM NEW."CarriedDepositAmount"
                   OR carry."Amount" IS DISTINCT FROM source_contract."DepositAmount"
                   OR carry."CompletedAt" IS NULL
                   OR NEW."CompletedAtUtc" > NOW()
                   OR NEW."CompletedAtUtc" < h."AttestedAtUtc"
                   OR NEW."CompletedAtUtc" < review."ReviewedAtUtc"
                THEN
                    RAISE EXCEPTION 'Committed transfer contradicts signed source, destination or carry-forward'
                      USING ERRCODE='23514';
                END IF;

                -- Source parties are IMMUTABLE history; destination is an
                -- exact, uniquely identified copy. No covert tenant swap.
                IF EXISTS (
                    SELECT t."TenantId",t."Role" FROM "BuildingManagementContractTenants" t
                     WHERE t."ContractId"=source_contract."Id" AND t."IsDeleted"=FALSE
                    EXCEPT
                    SELECT t."TenantId",t."Role" FROM "BuildingManagementContractTenants" t
                     WHERE t."ContractId"=target_contract."Id" AND t."IsDeleted"=FALSE
                ) OR EXISTS (
                    SELECT t."TenantId",t."Role" FROM "BuildingManagementContractTenants" t
                     WHERE t."ContractId"=target_contract."Id" AND t."IsDeleted"=FALSE
                    EXCEPT
                    SELECT t."TenantId",t."Role" FROM "BuildingManagementContractTenants" t
                     WHERE t."ContractId"=source_contract."Id" AND t."IsDeleted"=FALSE
                ) OR (SELECT count(*) FROM "BuildingManagementContractTenants"
                      WHERE "ContractId"=target_contract."Id" AND "IsDeleted"=FALSE)
                        <> roster."RosterCount" THEN
                    RAISE EXCEPTION 'Transfer tenant roster is incomplete or changed'
                      USING ERRCODE='23514';
                END IF;

                SELECT COALESCE(sum(s."Amount"),0) INTO other_held
                  FROM "BuildingManagementDepositSettlements" s
                 WHERE s."SourceContractId"=source_contract."Id"
                   AND s."Id"<>carry."Id";
                IF other_held <> 0 OR EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                     WHERE s."SourceContractId"=source_contract."Id"
                       AND s."Id"<>carry."Id"
                ) OR (
                    SELECT COALESCE(sum(s."Amount"),0)
                      FROM "BuildingManagementDepositSettlements" s
                     WHERE s."TargetContractId"=source_contract."Id"
                       AND s."Status"=2 AND s."Type"=3
                ) <> source_contract."DepositAmount"
                   OR EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                     WHERE s."TargetContractId"=source_contract."Id"
                       AND s."Status"<>2
                ) THEN
                    RAISE EXCEPTION 'Contract deposit carry-forward is duplicated or not funded'
                      USING ERRCODE='23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementRoomReservations"
                     WHERE "RoomId"=target_contract."RoomId"
                       AND "IsDeleted"=FALSE AND "Status" IN (1,2)
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementContracts"
                     WHERE "RoomId"=target_contract."RoomId"
                       AND "IsDeleted"=FALSE AND "Status" IN (2,5)
                       AND "Id"<>target_contract."Id"
                ) THEN
                    RAISE EXCEPTION 'Destination room has a conflicting booking or contract'
                      USING ERRCODE='23514';
                END IF;
                -- All monthly bills including the final source calendar month
                -- must be officially posted; every posted debt must be
                -- independently reviewed and paid with no unresolved delta.
                IF NOT EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                     WHERE i."ContractId"=source_contract."Id"
                       AND i."BillingYear"=EXTRACT(year FROM r."RequestedDate")::integer
                       AND i."BillingMonth"=EXTRACT(month FROM r."RequestedDate")::integer
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger" i
                     WHERE i."ContractId"=source_contract."Id" AND NOT EXISTS (
                         SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
                          WHERE o."ContractId"=i."ContractId"
                            AND o."IssuedInvoiceId"=i."Id" AND o."Kind"=1
                     )
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                     WHERE i."ContractId"=source_contract."Id" AND NOT EXISTS (
                         SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
                          WHERE o."ContractId"=i."ContractId"
                            AND o."IssuedInvoiceId"=i."Id" AND o."Kind"=2
                     )
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementOfficialInvoiceReceivables" o
                     WHERE o."ContractId"=source_contract."Id"
                       AND (o."OriginalAmount" <>
                         (SELECT COALESCE(sum(z."Amount"),0)
                            FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                           WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=TRUE)
                         OR EXISTS (
                          SELECT 1 FROM "BuildingManagementOfficialInvoiceReceiptReviews" z
                           WHERE z."ReceivablePostingId"=o."Id" AND z."Accepted"=FALSE)
                       )
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
                     WHERE "ContractId"=source_contract."Id"
                ) THEN
                    RAISE EXCEPTION 'Transfer requires source month fully issued and all debts settled'
                      USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END;
            $completion$;

            CREATE CONSTRAINT TRIGGER "TR_BM_RoomChangeCompletion_CommitGate"
              AFTER INSERT ON "BuildingManagementRoomChangeCompletions"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRoomChangeCommit"();

            CREATE FUNCTION "BM_VerifyRoomChangeTerminalRequest"()
            RETURNS trigger LANGUAGE plpgsql AS $terminal$
            BEGIN
                IF NEW."Status"=4 AND NOT EXISTS (
                    SELECT 1 FROM "BuildingManagementRoomChangeCompletions"
                     WHERE "RequestId"=NEW."Id"
                ) THEN
                    RAISE EXCEPTION 'Completed request requires atomic transfer ledger'
                      USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END;
            $terminal$;
            CREATE CONSTRAINT TRIGGER "TR_BM_RoomChangeRequest_CompletionGate"
              AFTER UPDATE ON "BuildingManagementRoomChangeRequests"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRoomChangeTerminalRequest"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeCompletions",
                       "BuildingManagementRoomChangeRequests"
                IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeCompletions" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeRequests"
                               WHERE "Status"=4 LIMIT 1) THEN
                    RAISE EXCEPTION 'Cannot downgrade signed room transfer history'
                      USING ERRCODE='23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangeRequest_CompletionGate"
              ON "BuildingManagementRoomChangeRequests";
            DROP FUNCTION "BM_VerifyRoomChangeTerminalRequest"();
            DROP TRIGGER "TR_BM_RoomChangeCompletion_CommitGate"
              ON "BuildingManagementRoomChangeCompletions";
            DROP FUNCTION "BM_VerifyRoomChangeCommit"();
            DROP TRIGGER "TR_BM_RoomChangeCompletion_NoTruncate"
              ON "BuildingManagementRoomChangeCompletions";
            DROP FUNCTION "BM_NoTruncateRoomChangeCompletions"();
            DROP TRIGGER "TR_BM_RoomChangeCompletion_Immutable"
              ON "BuildingManagementRoomChangeCompletions";
            DROP FUNCTION "BM_GuardRoomChangeCompletionHistory"();
            DROP TABLE "BuildingManagementRoomChangeCompletions";
            """);
        builder.Sql("""
            CREATE OR REPLACE FUNCTION "BM_GuardRoomChangeRequest"()
                        RETURNS trigger LANGUAGE plpgsql AS $room_change$
                        DECLARE source_contract record;
                        DECLARE source_building uuid;
                        DECLARE target_building uuid;
                        DECLARE source_status integer;
                        DECLARE target_status integer;
                        BEGIN
                            IF TG_OP = 'DELETE' THEN
                                RAISE EXCEPTION 'Room change review history cannot be deleted'
                                  USING ERRCODE = '23514';
                            ELSIF TG_OP = 'UPDATE' THEN
                                IF OLD."Status" IN (2,3)
                                   OR NEW."Id" IS DISTINCT FROM OLD."Id"
                                   OR NEW."SourceContractId" IS DISTINCT FROM OLD."SourceContractId"
                                   OR NEW."SourceRoomId" IS DISTINCT FROM OLD."SourceRoomId"
                                   OR NEW."TargetRoomId" IS DISTINCT FROM OLD."TargetRoomId"
                                   OR NEW."BuildingId" IS DISTINCT FROM OLD."BuildingId"
                                   OR NEW."RequestedDate" IS DISTINCT FROM OLD."RequestedDate"
                                   OR NEW."Reason" IS DISTINCT FROM OLD."Reason"
                                   OR NEW."RequestedByUserId" IS DISTINCT FROM OLD."RequestedByUserId"
                                   OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                                   OR NEW."Status" NOT IN (1,2,3)
                                   OR (OLD."Status" = 1 AND NEW."Status" NOT IN (2,3))
                                   OR (OLD."Status" = 1 AND NEW."Status" = 1)
                                   OR NEW."ReviewedAtUtc" < OLD."ReviewedAtUtc" THEN
                                    RAISE EXCEPTION 'Cannot rewrite or advance unauthorized room change'
                                      USING ERRCODE = '23514';
                                END IF;
                                RETURN NEW;
                            END IF;

                            -- Intake is NOT approval. Snapshot the current source and
                            -- target, including same-building invariants, under locks.
                            IF NEW."Status" <> 0 THEN
                                RAISE EXCEPTION 'New room change must start Pending'
                                  USING ERRCODE = '23514';
                            END IF;
                            SELECT "RoomId","Status","IsDeleted" INTO source_contract
                              FROM "BuildingManagementContracts"
                             WHERE "Id" = NEW."SourceContractId" FOR SHARE;
                            SELECT r."Status", f."BuildingId"
                              INTO source_status,source_building
                              FROM "BuildingManagementRooms" r
                              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                             WHERE r."Id" = NEW."SourceRoomId"
                               AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                             FOR SHARE OF r, f;
                            SELECT r."Status", f."BuildingId"
                              INTO target_status,target_building
                              FROM "BuildingManagementRooms" r
                              JOIN "BuildingManagementFloors" f ON f."Id" = r."FloorId"
                             WHERE r."Id" = NEW."TargetRoomId"
                               AND r."IsDeleted" = FALSE AND f."IsDeleted" = FALSE
                             FOR SHARE OF r, f;
                            IF source_contract."RoomId" IS DISTINCT FROM NEW."SourceRoomId"
                               OR source_contract."Status" IS DISTINCT FROM 2
                               OR source_contract."IsDeleted" IS DISTINCT FROM FALSE
                               OR source_status IS DISTINCT FROM 2
                               OR target_status IS DISTINCT FROM 1
                               OR source_building IS DISTINCT FROM NEW."BuildingId"
                               OR target_building IS DISTINCT FROM NEW."BuildingId"
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementMoveOutRequests" m
                                   WHERE m."ContractId" = NEW."SourceContractId"
                                     AND m."IsDeleted" = FALSE AND m."Status" IN (0,1))
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementContracts" c
                                   WHERE c."RoomId" = NEW."TargetRoomId"
                                     AND c."IsDeleted" = FALSE AND c."Status" IN (2,5))
                               OR EXISTS (
                                  SELECT 1 FROM "BuildingManagementRoomReservations" rr
                                   WHERE rr."RoomId" = NEW."TargetRoomId"
                                     AND rr."IsDeleted" = FALSE AND rr."Status" IN (1,2))
                            THEN
                                RAISE EXCEPTION 'Room change source or target is not eligible'
                                  USING ERRCODE = '23514';
                            END IF;
                            RETURN NEW;
                        END;
                        $room_change$;
            """);
        builder.Sql("""
            ALTER TABLE "BuildingManagementRoomChangeRequests"
              DROP CONSTRAINT "CK_BM_RoomChangeRequest_Valid";
            ALTER TABLE "BuildingManagementRoomChangeRequests"
              ADD CONSTRAINT "CK_BM_RoomChangeRequest_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "SourceRoomId" <> "TargetRoomId"
                    AND "Status" IN (0,1,2,3)
                    AND "RequestedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND length(btrim("Reason")) BETWEEN 8 AND 500
                    AND (("Status" = 0 AND "ReviewedAtUtc" IS NULL
                          AND "ReviewedByUserId" IS NULL AND "ReviewNotes" IS NULL)
                      OR ("Status" IN (1,2,3)
                          AND "ReviewedByUserId" IS NOT NULL
                          AND "ReviewedAtUtc" >= "CreatedAtUtc"
                          AND length(btrim("ReviewNotes")) BETWEEN 8 AND 500))
                );
            """);
    }
}
