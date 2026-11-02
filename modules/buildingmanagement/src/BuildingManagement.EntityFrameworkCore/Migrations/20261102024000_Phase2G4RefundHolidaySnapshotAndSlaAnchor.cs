using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// OI-04 refund deadline is seven Vietnam business days from the approved
/// final deposit reconciliation, not seven days from a later payee instruction.
/// Freeze the precise holiday set and make PostgreSQL recompute the due date.
/// Staging-only; legacy rows lacking the signed calendar set require explicit
/// migration review instead of silently being treated as "no holidays".
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102024000_Phase2G4RefundHolidaySnapshotAndSlaAnchor")]
public sealed class Phase2G4RefundHolidaySnapshotAndSlaAnchor : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        DO $guard$
        BEGIN
          IF EXISTS (SELECT 1 FROM
            "BuildingManagementContractDepositRefundInstructions" LIMIT 1)
          THEN
            RAISE EXCEPTION
              'Existing refund instructions lack a frozen holiday set; reconcile before migration'
              USING ERRCODE='23514';
          END IF;
        END $guard$;

        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          ADD COLUMN "StatutoryHolidaysJson" text NOT NULL;

        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          DROP CONSTRAINT "CK_BM_RefundDestinationFields";

        -- DueOn may already be overdue if the Owner delays destination
        -- approval. Never anchor it to the instruction timestamp.
        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          ADD CONSTRAINT "CK_BM_RefundDestinationFields" CHECK (
            length(btrim("PayeeName")) BETWEEN 3 AND 200 AND
            length(btrim("PayeeBank")) BETWEEN 3 AND 100 AND
            length(btrim("PayeeAccount")) BETWEEN 6 AND 40 AND
            length(btrim("SignedDestinationReference")) BETWEEN 12 AND 500 AND
            length(btrim("HolidayCalendarReference")) BETWEEN 12 AND 500 AND
            length("StatutoryHolidaysJson") BETWEEN 2 AND 4000 AND
            jsonb_typeof("StatutoryHolidaysJson"::jsonb)='array' AND
            "DueOn" >= DATE '2000-01-01');

        CREATE FUNCTION "BM_VerifyRefundHolidaySnapshotSla"()
        RETURNS trigger LANGUAGE plpgsql AS $sla$
        DECLARE signed record;
        DECLARE start_day date;
        DECLARE holiday_text text;
        DECLARE expected_due date;
        DECLARE holiday_count integer;
        DECLARE distinct_holiday_count integer;
        BEGIN
          IF TG_OP <> 'INSERT' OR
             current_setting('transaction_isolation') <> 'serializable' THEN
            RAISE EXCEPTION 'Refund holiday snapshot is immutable and serializable'
              USING ERRCODE='23514';
          END IF;

          SELECT * INTO signed FROM
            "BuildingManagementContractDepositSettlementDecisions"
            WHERE "Id"=NEW."DecisionId"
              AND "ContractId"=NEW."ContractId"
              AND "MoveOutRequestId"=NEW."MoveOutRequestId"
              AND "BuildingId"=NEW."BuildingId"
            FOR SHARE;

          IF signed."Id" IS NULL OR
             signed."ApprovedByUserId" IS DISTINCT FROM NEW."OwnerId" OR
             signed."ApprovedRefundAmount" IS DISTINCT FROM
                 NEW."ApprovedRefundAmountVnd" OR
             NEW."ApprovedAtUtc" < signed."ApprovedAtUtc" OR
             jsonb_typeof(NEW."StatutoryHolidaysJson"::jsonb) <> 'array'
          THEN
            RAISE EXCEPTION 'No consistent signed refund/calendar scope'
              USING ERRCODE='23514';
          END IF;

          start_day := (signed."ApprovedAtUtc" AT TIME ZONE
            'Asia/Ho_Chi_Minh')::date;

          FOR holiday_text IN SELECT jsonb_array_elements_text(
              NEW."StatutoryHolidaysJson"::jsonb)
          LOOP
            IF holiday_text !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR
               holiday_text::date < start_day OR
               holiday_text::date > start_day + 30
            THEN
              RAISE EXCEPTION 'Invalid Vietnam holiday date in frozen calendar'
                USING ERRCODE='23514';
            END IF;
          END LOOP;

          SELECT count(*),count(DISTINCT h.day)
            INTO holiday_count,distinct_holiday_count
            FROM jsonb_array_elements_text(
              NEW."StatutoryHolidaysJson"::jsonb) AS h(day);
          IF holiday_count > 80 OR
             holiday_count <> distinct_holiday_count THEN
            RAISE EXCEPTION 'Duplicate or excessive holiday dates'
              USING ERRCODE='23514';
          END IF;

          SELECT d.day::date INTO expected_due
            FROM generate_series((start_day+1)::timestamp,
              (start_day+45)::timestamp,INTERVAL '1 day') AS d(day)
           WHERE EXTRACT(ISODOW FROM d.day) < 6 AND
             NOT EXISTS (
               SELECT 1 FROM jsonb_array_elements_text(
                 NEW."StatutoryHolidaysJson"::jsonb) AS h(day)
               WHERE h.day = to_char(d.day,'YYYY-MM-DD')
             )
           ORDER BY d.day
           OFFSET 6 LIMIT 1;

          IF expected_due IS NULL OR expected_due > start_day+30 OR
             NEW."DueOn" IS DISTINCT FROM expected_due THEN
            RAISE EXCEPTION
              'Refund deadline must be seven business days from final reconciliation'
              USING ERRCODE='23514';
          END IF;

          RETURN NEW;
        END $sla$;

        CREATE TRIGGER "TR_BM_VerifyRefundHolidaySnapshotSla"
          BEFORE INSERT ON "BuildingManagementContractDepositRefundInstructions"
          FOR EACH ROW EXECUTE FUNCTION "BM_VerifyRefundHolidaySnapshotSla"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementContractDepositRefundInstructions"
          IN ACCESS EXCLUSIVE MODE;
        DO $rollback$
        BEGIN
          IF EXISTS (SELECT 1 FROM
            "BuildingManagementContractDepositRefundInstructions" LIMIT 1)
          THEN
            RAISE EXCEPTION
              'Cannot discard signed holiday snapshots with existing refund history'
              USING ERRCODE='23514';
          END IF;
        END $rollback$;

        DROP TRIGGER "TR_BM_VerifyRefundHolidaySnapshotSla"
          ON "BuildingManagementContractDepositRefundInstructions";
        DROP FUNCTION "BM_VerifyRefundHolidaySnapshotSla"();

        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          DROP CONSTRAINT "CK_BM_RefundDestinationFields";

        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          ADD CONSTRAINT "CK_BM_RefundDestinationFields" CHECK (
            length(btrim("PayeeName")) BETWEEN 3 AND 200 AND
            length(btrim("PayeeBank")) BETWEEN 3 AND 100 AND
            length(btrim("PayeeAccount")) BETWEEN 6 AND 40 AND
            length(btrim("SignedDestinationReference")) BETWEEN 12 AND 500 AND
            length(btrim("HolidayCalendarReference")) BETWEEN 12 AND 500 AND
            "DueOn" >= ("ApprovedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date);

        ALTER TABLE "BuildingManagementContractDepositRefundInstructions"
          DROP COLUMN "StatutoryHolidaysJson";
        """);
}
