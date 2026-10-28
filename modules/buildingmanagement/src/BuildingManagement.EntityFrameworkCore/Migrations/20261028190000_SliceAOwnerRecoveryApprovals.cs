using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive, review-only Slice A recovery evidence ledger. No recovery API,
/// approval seed, runtime role grant or production activation. Populated
/// downgrade deliberately fails, preserving approvals and consumption audit.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261028190000_SliceAOwnerRecoveryApprovals")]
public sealed class SliceAOwnerRecoveryApprovals : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementOwnerRecoverySignoffs" (
                "EvidenceId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "FoundingOwnerId" uuid NOT NULL,
                "NewOwnerId" uuid NOT NULL,
                "ApprovedByUserId" uuid NOT NULL,
                "ApprovedAtUtc" timestamptz NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL,
                "ConsumedAtUtc" timestamptz NULL,
                "RecoveryGrantId" uuid NULL,
                CONSTRAINT "PK_BM_RecoverySignoffs"
                    PRIMARY KEY ("EvidenceId", "ApprovedByUserId"),
                CONSTRAINT "FK_BM_RecoverySignoffs_Building"
                    FOREIGN KEY ("BuildingId") REFERENCES "Buildings" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_RecoverySignoffs_Ids" CHECK (
                    "EvidenceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "FoundingOwnerId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "NewOwnerId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ApprovedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "FoundingOwnerId" <> "NewOwnerId"
                    AND "ApprovedByUserId" <> "NewOwnerId"),
                CONSTRAINT "CK_BM_RecoverySignoffs_Time"
                    CHECK ("ExpiresAtUtc" > "ApprovedAtUtc"),
                CONSTRAINT "CK_BM_RecoverySignoffs_Consumed"
                    CHECK (("ConsumedAtUtc" IS NULL AND "RecoveryGrantId" IS NULL)
                        OR ("ConsumedAtUtc" IS NOT NULL AND "RecoveryGrantId" IS NOT NULL))
            );
            CREATE INDEX "IX_BM_RecoverySignoffs_Target"
                ON "BuildingManagementOwnerRecoverySignoffs"
                ("BuildingId", "NewOwnerId", "EvidenceId");

            CREATE FUNCTION "BM_GuardRecoverySignoffHistory"()
            RETURNS trigger LANGUAGE plpgsql AS $recovery_guard$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Recovery signoff evidence is append-only'
                        USING ERRCODE = '23514';
                END IF;
                IF OLD."ConsumedAtUtc" IS NOT NULL
                   OR NEW."EvidenceId" IS DISTINCT FROM OLD."EvidenceId"
                   OR NEW."BuildingId" IS DISTINCT FROM OLD."BuildingId"
                   OR NEW."FoundingOwnerId" IS DISTINCT FROM OLD."FoundingOwnerId"
                   OR NEW."NewOwnerId" IS DISTINCT FROM OLD."NewOwnerId"
                   OR NEW."ApprovedByUserId" IS DISTINCT FROM OLD."ApprovedByUserId"
                   OR NEW."ApprovedAtUtc" IS DISTINCT FROM OLD."ApprovedAtUtc"
                   OR NEW."ExpiresAtUtc" IS DISTINCT FROM OLD."ExpiresAtUtc"
                   OR NEW."ConsumedAtUtc" IS NULL
                   OR NEW."RecoveryGrantId" IS NULL THEN
                    RAISE EXCEPTION 'Recovery signoff evidence cannot be rewritten'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $recovery_guard$;
            CREATE TRIGGER "TR_BM_RecoverySignoff_ConsumeOnly"
            BEFORE UPDATE OR DELETE ON "BuildingManagementOwnerRecoverySignoffs"
            FOR EACH ROW EXECUTE FUNCTION "BM_GuardRecoverySignoffHistory"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementOwnerRecoverySignoffs"
                IN ACCESS EXCLUSIVE MODE;
            DO $recovery_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementOwnerRecoverySignoffs" LIMIT 1) THEN
                    RAISE EXCEPTION 'Recovery signoff history exists; refuse rollback';
                END IF;
            END;
            $recovery_down$;
            DROP TABLE "BuildingManagementOwnerRecoverySignoffs";
            DROP FUNCTION "BM_GuardRecoverySignoffHistory"();
            """);
    }
}
