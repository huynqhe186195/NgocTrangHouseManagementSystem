using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Fix Phase 2H's deferred commit-time evidence clock. PostgreSQL NOW() is
/// anchored at transaction BEGIN, so a legitimate CompletedAtUtc recorded
/// *inside* that transaction is falsely classified as future-dated. The
/// wall clock is checked at the deferred commit boundary instead. This is
/// additive for already-migrated isolated staging databases; never rewrites
/// the 2G.5 end authorization or loosens any other 2H evidence predicate.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029236000_Phase2HWallClockCompletionValidation")]
public sealed class Phase2HWallClockCompletionValidation : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Full explicit replacement is intentional: the old definition
            -- used transaction_timestamp (NOW), not commit wall time.
            CREATE OR REPLACE FUNCTION "BM_VerifyRoomChangeCommit"()
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
                   OR NEW."CompletedAtUtc" > clock_timestamp()
                   OR NEW."CompletedAtUtc" < h."AttestedAtUtc"
                   OR NEW."CompletedAtUtc" < review."ReviewedAtUtc"
                THEN
                    RAISE EXCEPTION 'Committed transfer contradicts signed source, destination or carry-forward'
                      USING ERRCODE='23514';
                END IF;

                -- Check the domain's canonical SHA256 at the SQL boundary.
                -- timestamptz precision is 6 digits; the .NET domain hashes
                -- its UTC timestamp with 7 decimal places (last digit 0).
                IF NEW."EvidenceSha256" IS DISTINCT FROM encode(
                    sha256(convert_to(concat_ws(E'\n',
                        NEW."Id"::text,NEW."RequestId"::text,
                        NEW."SourceContractId"::text,NEW."TargetContractId"::text,
                        NEW."SourceRoomId"::text,NEW."TargetRoomId"::text,
                        NEW."BuildingId"::text,NEW."PolicyId"::text,
                        NEW."PolicyReviewId"::text,NEW."RosterSnapshotId"::text,
                        NEW."HandoverEvidenceId"::text,
                        NEW."CarryForwardSettlementId"::text,
                        NEW."CarriedDepositAmount"::numeric(18,0)::text,
                        NEW."SourceRoomDisposition"::text,
                        NEW."SourceDispositionEvidenceReference",
                        NEW."CompletionReference",
                        NEW."CompletedByUserId"::text,
                        to_char(NEW."CompletedAtUtc" AT TIME ZONE 'UTC',
                            'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z'
                    ),'UTF8')),'hex') THEN
                    RAISE EXCEPTION 'Completed room change checksum does not match source'
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
                -- A completed allocation is not proof of real money unless
                -- the exact original Payment was completed, was not deleted,
                -- and has a one-and-only-one reservation/contract source.
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                    LEFT JOIN "BuildingManagementPayments" pay ON pay."Id"=s."PaymentId"
                     WHERE s."TargetContractId"=source_contract."Id"
                       AND s."Type"=3 AND
                         (s."Status"<>2 OR s."PaymentId" IS NULL
                          OR pay."Id" IS NULL OR pay."IsDeleted"=TRUE
                          OR pay."Status"<>2 OR pay."PaidAt" IS NULL
                          OR pay."Amount"<>s."Amount"
                          OR pay."DepositDisposition" NOT IN (1,2)
                          OR NOT (
                           (pay."Purpose"=2 AND pay."ContractId"=source_contract."Id"
                            AND s."SourceRoomReservationId" IS NULL)
                           OR
                           (pay."Purpose"=1 AND pay."RoomReservationId" IS NOT NULL
                            AND pay."RoomReservationId"=s."SourceRoomReservationId")
                          )
                          OR (SELECT count(*) FROM "BuildingManagementDepositSettlements" d
                               WHERE d."PaymentId"=pay."Id")<>1)
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments" pay
                     WHERE pay."ContractId"=source_contract."Id"
                       AND pay."Purpose"=2 AND pay."Status"=2
                       AND NOT EXISTS (
                           SELECT 1 FROM "BuildingManagementDepositSettlements" s
                            WHERE s."PaymentId"=pay."Id"
                              AND s."TargetContractId"=source_contract."Id"
                              AND s."Type"=3 AND s."Status"=2)
                ) THEN
                    RAISE EXCEPTION 'Transfer source deposit lacks exclusive verified original receipts'
                      USING ERRCODE='23514';
                END IF;
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
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeCompletions"
              IN ACCESS EXCLUSIVE MODE;
            DO $clock_rollback$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeCompletions" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Completed 2H transfer exists; refuse old clock guard rollback'
                      USING ERRCODE='23514';
                END IF;
            END;
            $clock_rollback$;
            CREATE OR REPLACE FUNCTION "BM_VerifyRoomChangeCommit"()
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

                -- Check the domain's canonical SHA256 at the SQL boundary.
                -- timestamptz precision is 6 digits; the .NET domain hashes
                -- its UTC timestamp with 7 decimal places (last digit 0).
                IF NEW."EvidenceSha256" IS DISTINCT FROM encode(
                    sha256(convert_to(concat_ws(E'\n',
                        NEW."Id"::text,NEW."RequestId"::text,
                        NEW."SourceContractId"::text,NEW."TargetContractId"::text,
                        NEW."SourceRoomId"::text,NEW."TargetRoomId"::text,
                        NEW."BuildingId"::text,NEW."PolicyId"::text,
                        NEW."PolicyReviewId"::text,NEW."RosterSnapshotId"::text,
                        NEW."HandoverEvidenceId"::text,
                        NEW."CarryForwardSettlementId"::text,
                        NEW."CarriedDepositAmount"::numeric(18,0)::text,
                        NEW."SourceRoomDisposition"::text,
                        NEW."SourceDispositionEvidenceReference",
                        NEW."CompletionReference",
                        NEW."CompletedByUserId"::text,
                        to_char(NEW."CompletedAtUtc" AT TIME ZONE 'UTC',
                            'YYYY-MM-DD"T"HH24:MI:SS.US') || '0Z'
                    ),'UTF8')),'hex') THEN
                    RAISE EXCEPTION 'Completed room change checksum does not match source'
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
                -- A completed allocation is not proof of real money unless
                -- the exact original Payment was completed, was not deleted,
                -- and has a one-and-only-one reservation/contract source.
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementDepositSettlements" s
                    LEFT JOIN "BuildingManagementPayments" pay ON pay."Id"=s."PaymentId"
                     WHERE s."TargetContractId"=source_contract."Id"
                       AND s."Type"=3 AND
                         (s."Status"<>2 OR s."PaymentId" IS NULL
                          OR pay."Id" IS NULL OR pay."IsDeleted"=TRUE
                          OR pay."Status"<>2 OR pay."PaidAt" IS NULL
                          OR pay."Amount"<>s."Amount"
                          OR pay."DepositDisposition" NOT IN (1,2)
                          OR NOT (
                           (pay."Purpose"=2 AND pay."ContractId"=source_contract."Id"
                            AND s."SourceRoomReservationId" IS NULL)
                           OR
                           (pay."Purpose"=1 AND pay."RoomReservationId" IS NOT NULL
                            AND pay."RoomReservationId"=s."SourceRoomReservationId")
                          )
                          OR (SELECT count(*) FROM "BuildingManagementDepositSettlements" d
                               WHERE d."PaymentId"=pay."Id")<>1)
                ) OR EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments" pay
                     WHERE pay."ContractId"=source_contract."Id"
                       AND pay."Purpose"=2 AND pay."Status"=2
                       AND NOT EXISTS (
                           SELECT 1 FROM "BuildingManagementDepositSettlements" s
                            WHERE s."PaymentId"=pay."Id"
                              AND s."TargetContractId"=source_contract."Id"
                              AND s."Type"=3 AND s."Status"=2)
                ) THEN
                    RAISE EXCEPTION 'Transfer source deposit lacks exclusive verified original receipts'
                      USING ERRCODE='23514';
                END IF;
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
            """);
    }
}
