using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// OI-07 choices are explicit, source-scoped, append-only PROPOSALS.
/// This migration intentionally does NOT open an execution path.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029231000_Phase2HBusinessPolicyProposals")]
public sealed class Phase2HBusinessPolicyProposals : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeBusinessPolicies" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "RequestId" uuid NOT NULL
                    REFERENCES "BuildingManagementRoomChangeRequests" ("Id")
                    ON DELETE RESTRICT,
                "SourceContractId" uuid NOT NULL
                    REFERENCES "BuildingManagementContracts" ("Id")
                    ON DELETE RESTRICT,
                "BuildingId" uuid NOT NULL,
                "RosterSnapshotId" uuid NOT NULL
                    REFERENCES "BuildingManagementRoomChangeTenantRosterSnapshots" ("Id")
                    ON DELETE RESTRICT,
                "RosterSha256" varchar(64) NOT NULL,
                "ContractTreatment" integer NOT NULL,
                "PricingTreatment" integer NOT NULL,
                "DepositTreatment" integer NOT NULL,
                "TenantTreatment" integer NOT NULL,
                "ContractPolicyReference" varchar(500) NOT NULL,
                "PricingPolicyReference" varchar(500) NOT NULL,
                "DepositPolicyReference" varchar(500) NOT NULL,
                "TenantConsentReference" varchar(500) NOT NULL,
                "ProposedByUserId" uuid NOT NULL,
                "ProposedAtUtc" timestamptz NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "CK_BM_RoomChangePolicy_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ProposedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ContractTreatment" IN (1,2)
                    AND "PricingTreatment" IN (1,2)
                    AND "DepositTreatment" IN (1,2,3)
                    AND "TenantTreatment" IN (1,2)
                    AND length("RosterSha256")=64 AND length("EvidenceSha256")=64
                    AND "RosterSha256" ~ '^[0-9a-f]{64}$'
                    AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
                    AND length(btrim("ContractPolicyReference")) BETWEEN 12 AND 500
                    AND length(btrim("PricingPolicyReference")) BETWEEN 12 AND 500
                    AND length(btrim("DepositPolicyReference")) BETWEEN 12 AND 500
                    AND length(btrim("TenantConsentReference")) BETWEEN 12 AND 500
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangePolicy_Request"
              ON "BuildingManagementRoomChangeBusinessPolicies" ("RequestId");

            CREATE FUNCTION "BM_GuardRoomChangeBusinessPolicy"()
            RETURNS trigger LANGUAGE plpgsql AS $policy$
            DECLARE req record;
            DECLARE roster record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Room-change OI-07 proposal is immutable'
                       USING ERRCODE = '23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'OI-07 proposal requires serializable transaction'
                       USING ERRCODE = '23514';
                END IF;
                SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
                  WHERE "Id" = NEW."RequestId" FOR SHARE;
                SELECT * INTO roster
                  FROM "BuildingManagementRoomChangeTenantRosterSnapshots"
                  WHERE "Id" = NEW."RosterSnapshotId" FOR SHARE;
                IF req."Id" IS NULL OR roster."Id" IS NULL
                   OR req."Status" NOT IN (0,1)
                   OR NEW."SourceContractId" IS DISTINCT FROM req."SourceContractId"
                   OR NEW."BuildingId" IS DISTINCT FROM req."BuildingId"
                   OR roster."RoomChangeRequestId" IS DISTINCT FROM req."Id"
                   OR roster."SourceContractId" IS DISTINCT FROM req."SourceContractId"
                   OR roster."SourceRoomId" IS DISTINCT FROM req."SourceRoomId"
                   OR roster."BuildingId" IS DISTINCT FROM req."BuildingId"
                   OR NEW."RosterSha256" IS DISTINCT FROM roster."RosterSha256"
                   OR NEW."ProposedAtUtc" < roster."CapturedAtUtc" THEN
                    RAISE EXCEPTION 'OI-07 proposal does not match source request and roster'
                       USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $policy$;
            CREATE TRIGGER "TR_BM_RoomChangePolicy_Guard"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeBusinessPolicies"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeBusinessPolicy"();

            CREATE FUNCTION "BM_NoTruncateRoomChangePolicies"()
            RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                RAISE EXCEPTION 'OI-07 proposal evidence is retained'
                    USING ERRCODE = '23514';
            END;
            $hold$;
            CREATE TRIGGER "TR_BM_RoomChangePolicy_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeBusinessPolicies"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangePolicies"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeBusinessPolicies"
                IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementRoomChangeBusinessPolicies" LIMIT 1) THEN
                    RAISE EXCEPTION 'Cannot discard recorded OI-07 proposals'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangePolicy_NoTruncate"
              ON "BuildingManagementRoomChangeBusinessPolicies";
            DROP FUNCTION "BM_NoTruncateRoomChangePolicies"();
            DROP TRIGGER "TR_BM_RoomChangePolicy_Guard"
              ON "BuildingManagementRoomChangeBusinessPolicies";
            DROP FUNCTION "BM_GuardRoomChangeBusinessPolicy"();
            DROP TABLE "BuildingManagementRoomChangeBusinessPolicies";
            """);
    }
}
