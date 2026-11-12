using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// D04/H4 documentary intent integrity. The earlier append-only table
/// checked only SHA-256 format; this additive migration validates the exact
/// Domain canonical bytes, including null due-date and UTC microseconds.
/// This does not verify a real bank receipt, signature or allow cash posting.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104170000_Phase2HDepositDispositionIntentDigestIntegrity")]
public sealed class Phase2HDepositDispositionIntentDigestIntegrity : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE FUNCTION "BM_CalculateRoomChangeDepositDispositionSha"(
          h "BuildingManagementRoomChangeDepositDispositionIntents"
        ) RETURNS text LANGUAGE sql AS $sha$
          SELECT encode(sha256(convert_to(concat_ws(E'\n',
            h."Id"::text,
            h."RequestId"::text,
            h."SourceContractId"::text,
            h."TargetContractId"::text,
            h."BuildingId"::text,
            to_char(h."TransferDateVietnam"::timestamp,'YYYY-MM-DD'),
            h."ClaimedOriginalSourceHeldVnd"::numeric(18,0)::text,
            h."AlreadyPostedApprovedDeductionsVnd"::numeric(18,0)::text,
            h."ProposedCarryTargetVnd"::numeric(18,0)::text,
            h."TargetRequiredDepositVnd"::numeric(18,0)::text,
            h."ProposedUnpaidTopUpVnd"::numeric(18,0)::text,
            coalesce(to_char(h."ProposedTopUpDueDateVietnam"::timestamp,'YYYY-MM-DD'),''),
            h."ProposedFutureRentCreditVnd"::numeric(18,0)::text,
            h."ProposedManualRefundVnd"::numeric(18,0)::text,
            h."SurplusChoice"::text,
            h."OriginalCashLedgerReference",
            h."TargetDepositPolicyReference",
            coalesce(h."SignedDeductionReference",''),
            h."OwnerSignedDispositionReference",
            h."ProposedByOwnerId"::text,
            to_char(h."ProposedAtUtc" AT TIME ZONE 'UTC',
              'YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z'
          ),'UTF8')),'hex');
        $sha$;

        -- Never rewrite signed history to make a new migration succeed.
        -- Existing invalid records must be independently investigated.
        DO $history$
        BEGIN
          IF EXISTS(
            SELECT 1
              FROM "BuildingManagementRoomChangeDepositDispositionIntents" h
             WHERE h."EvidenceSha256" IS DISTINCT FROM
                   "BM_CalculateRoomChangeDepositDispositionSha"(h)
          ) THEN
            RAISE EXCEPTION 'H4 original intent history has an invalid canonical digest'
              USING ERRCODE='23514';
          END IF;
        END;
        $history$;

        CREATE FUNCTION "BM_VerifyRoomChangeDepositDispositionSha"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        BEGIN
          IF NEW."EvidenceSha256" IS DISTINCT FROM
             "BM_CalculateRoomChangeDepositDispositionSha"(NEW) THEN
            RAISE EXCEPTION 'H4 original intent payload digest mismatch'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $guard$;

        CREATE TRIGGER "TR_BM_2H_H4Intent_Digest"
          BEFORE INSERT
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
          FOR EACH ROW EXECUTE FUNCTION
            "BM_VerifyRoomChangeDepositDispositionSha"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDepositDispositionIntents"
          IN ACCESS EXCLUSIVE MODE;
        DO $retain$
        BEGIN
          IF EXISTS(
            SELECT 1
              FROM "BuildingManagementRoomChangeDepositDispositionIntents"
             LIMIT 1
          ) THEN
            RAISE EXCEPTION 'Cannot remove H4 digest guard with original intent history'
              USING ERRCODE='23514';
          END IF;
        END;
        $retain$;
        DROP TRIGGER "TR_BM_2H_H4Intent_Digest"
          ON "BuildingManagementRoomChangeDepositDispositionIntents";
        DROP FUNCTION "BM_VerifyRoomChangeDepositDispositionSha"();
        DROP FUNCTION "BM_CalculateRoomChangeDepositDispositionSha"(
          "BuildingManagementRoomChangeDepositDispositionIntents");
        """);
}
