using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive D01/D02 execution-only fence. Does not rewrite the original
/// signed pricing records or previously-applied 2H migration bodies.
/// Historical candidates may remain source-day/truncation, but new completed
/// transfers require the Owner-confirmed target-day, whole-VND rule.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104130000_Phase2HApprovedTargetDayCommitFence")]
public sealed class Phase2HApprovedTargetDayCommitFence : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE FUNCTION "BM_2HVerifyApprovedTargetDay"()
        RETURNS trigger LANGUAGE plpgsql AS $d01$
        DECLARE
         req record;
         pricing record;
         month_days integer;
         old_days integer;
        BEGIN
         SELECT "Id","RequestedDate" INTO req
           FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId";
         SELECT "Id","RequestId","TransferDate","TransferDayOwner",
                "Rounding","CalendarDays","SourceChargeableDays",
                "TargetChargeableDays","ProposedTargetRentVnd"
           INTO pricing
           FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
          WHERE "Id"=NEW."PricingRuleId";

         IF req."Id" IS NULL OR pricing."Id" IS NULL
            OR req."RequestedDate" IS NULL THEN
          RAISE EXCEPTION 'Missing original request or signed day rule'
           USING ERRCODE='23514';
         END IF;

         month_days:=extract(day from
            (date_trunc('month',req."RequestedDate"::timestamp)
                 + interval '1 month - 1 day'))::integer;
         old_days:=extract(day from req."RequestedDate")::integer-1;

         IF pricing."RequestId" IS DISTINCT FROM req."Id"
            OR pricing."TransferDate" IS DISTINCT FROM req."RequestedDate"
            OR pricing."TransferDayOwner" IS DISTINCT FROM 2
            OR pricing."Rounding" IS DISTINCT FROM 1
            OR pricing."CalendarDays" IS DISTINCT FROM month_days
            OR pricing."SourceChargeableDays" IS DISTINCT FROM old_days
            OR pricing."TargetChargeableDays" IS DISTINCT FROM month_days-old_days
            OR NEW."TargetFirstMonthRentVnd" IS DISTINCT FROM
                pricing."ProposedTargetRentVnd" THEN
          RAISE EXCEPTION
           'Owner-approved D01/D02 requires target handover day and midpoint-up whole-VND pricing'
           USING ERRCODE='23514';
         END IF;
         RETURN NULL;
        END;
        $d01$;

        CREATE CONSTRAINT TRIGGER "TR_BM_2H_ApprovedTargetDay_Commit"
         AFTER INSERT ON "BuildingManagementRoomChangeMidMonthCompletions"
         DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION "BM_2HVerifyApprovedTargetDay"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeMidMonthCompletions"
         IN ACCESS EXCLUSIVE MODE;
        DO $retained$
        BEGIN
         IF EXISTS (
          SELECT 1 FROM "BuildingManagementRoomChangeMidMonthCompletions"
           LIMIT 1
         ) THEN
          RAISE EXCEPTION 'Refuse to discard accepted D01/D02 transfer history'
           USING ERRCODE='23514';
         END IF;
        END;
        $retained$;
        DROP TRIGGER "TR_BM_2H_ApprovedTargetDay_Commit"
         ON "BuildingManagementRoomChangeMidMonthCompletions";
        DROP FUNCTION "BM_2HVerifyApprovedTargetDay"();
        """);
}
