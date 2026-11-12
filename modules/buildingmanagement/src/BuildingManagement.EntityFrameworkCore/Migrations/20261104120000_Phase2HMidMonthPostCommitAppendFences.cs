using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Phase 2H append fences: a mid-month completion is not sufficient if a later
/// INSERT can silently create another occupant or re-spend its source cash.
/// Deferred checks also permit initial target-roster and carry INSERTs in the
/// very same atomic transfer transaction, after their final shape is verified.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104120000_Phase2HMidMonthPostCommitAppendFences")]
public sealed class Phase2HMidMonthPostCommitAppendFences : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE FUNCTION "BM_2HMidVerifyRosterAppend"()
        RETURNS trigger LANGUAGE plpgsql AS $roster$
        DECLARE link record;
        BEGIN
         -- Deleted historical entries do not modify the active tenant roster.
         IF NEW."IsDeleted" IS DISTINCT FROM FALSE THEN
          RETURN NULL;
         END IF;

         FOR link IN
          SELECT m."SourceContractId", m."TargetContractId", r."RosterCount"
            FROM "BuildingManagementRoomChangeMidMonthCompletions" m
            JOIN "BuildingManagementRoomChangeTenantRosterSnapshots" r
              ON r."Id"=m."RosterSnapshotId"
           WHERE m."SourceContractId"=NEW."ContractId"
              OR m."TargetContractId"=NEW."ContractId"
         LOOP
          IF (SELECT count(*) FROM "BuildingManagementContractTenants" t
               WHERE t."ContractId"=link."SourceContractId"
                 AND t."IsDeleted"=FALSE) IS DISTINCT FROM link."RosterCount"
             OR (SELECT count(*) FROM "BuildingManagementContractTenants" t
               WHERE t."ContractId"=link."TargetContractId"
                 AND t."IsDeleted"=FALSE) IS DISTINCT FROM link."RosterCount"
          THEN
           RAISE EXCEPTION 'Completed mid-month tenant roster cannot be appended'
            USING ERRCODE='23514';
          END IF;
         END LOOP;
         RETURN NULL;
        END;
        $roster$;

        CREATE CONSTRAINT TRIGGER "TR_BM_2HMid_RosterAppend"
         AFTER INSERT ON "BuildingManagementContractTenants"
         DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION "BM_2HMidVerifyRosterAppend"();

        CREATE FUNCTION "BM_2HMidVerifyCashAppend"()
        RETURNS trigger LANGUAGE plpgsql AS $cash$
        BEGIN
         -- An original full carry may be inserted in the atomic transfer.
         -- Every other later source disposition, or new incoming deposit to
         -- an already completed source, contradicts the frozen cash balance.
         IF EXISTS (
          SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions" m
           WHERE (NEW."SourceContractId"=m."SourceContractId"
                   AND NEW."Id"<>m."CarryForwardSettlementId")
              OR NEW."TargetContractId"=m."SourceContractId"
         ) THEN
          RAISE EXCEPTION 'Completed mid-month source cash cannot be reallocated'
           USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $cash$;

        CREATE CONSTRAINT TRIGGER "TR_BM_2HMid_CashAppend"
         AFTER INSERT ON "BuildingManagementDepositSettlements"
         DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION "BM_2HMidVerifyCashAppend"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        DO $retained$
        BEGIN
         IF EXISTS (SELECT 1
                      FROM "BuildingManagementRoomChangeMidMonthCompletions")
         THEN
          RAISE EXCEPTION 'Refuse removal of post-commit append fences with completed transfers'
           USING ERRCODE='23514';
         END IF;
        END;
        $retained$;

        DROP TRIGGER "TR_BM_2HMid_CashAppend"
          ON "BuildingManagementDepositSettlements";
        DROP FUNCTION "BM_2HMidVerifyCashAppend"();
        DROP TRIGGER "TR_BM_2HMid_RosterAppend"
          ON "BuildingManagementContractTenants";
        DROP FUNCTION "BM_2HMidVerifyRosterAppend"();
        """);
}
