using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Reviewed Slice A source migration, NOT deployed. Operational rollout
/// requires explicit migration approval and a fresh backup. No automatic
/// schema changes in app startup, no permission seeds, no administrator
/// wildcard. Down refuses ANY historical membership, including revoked.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261028121500_SliceAOperatorBuildingMembership")]
public sealed class SliceAOperatorBuildingMembership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "BuildingManagementOperatorBuildingGrants" (
    "Id" uuid NOT NULL,
    "OperatorUserId" uuid NOT NULL,
    "BuildingId" uuid NOT NULL,
    "GrantedByUserId" uuid NOT NULL,
    "GrantedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NULL,
    "RevokedAtUtc" timestamp with time zone NULL,
    "RevokedByUserId" uuid NULL,
    "RevisionNumber" integer NOT NULL,
    "ExtraProperties" text NOT NULL,
    "ConcurrencyStamp" character varying(40) NOT NULL,
    CONSTRAINT "PK_BM_OperatorBuildingGrants" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BM_OperatorBuildingGrants_Building"
        FOREIGN KEY ("BuildingId") REFERENCES "Buildings" ("Id")
        ON DELETE RESTRICT,
    CONSTRAINT "CK_BM_Grant_Nonempty_Ids" CHECK (
        "Id" <> '00000000-0000-0000-0000-000000000000'::uuid AND
        "OperatorUserId" <> '00000000-0000-0000-0000-000000000000'::uuid AND
        "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid AND
        "GrantedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
    ),
    CONSTRAINT "CK_BM_Grant_Revision" CHECK ("RevisionNumber" >= 1),
    CONSTRAINT "CK_BM_Grant_Expiry" CHECK (
        "ExpiresAtUtc" IS NULL OR "ExpiresAtUtc" > "GrantedAtUtc"
    ),
    CONSTRAINT "CK_BM_Grant_Revocation" CHECK (
        ("RevokedAtUtc" IS NULL AND "RevokedByUserId" IS NULL)
        OR
        ("RevokedAtUtc" IS NOT NULL AND
         "RevokedAtUtc" >= "GrantedAtUtc" AND
         "RevokedByUserId" IS NOT NULL AND
         "RevokedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid)
    )
);

CREATE UNIQUE INDEX "UX_BM_OperatorBuilding_Unrevoked"
    ON "BuildingManagementOperatorBuildingGrants"
    ("OperatorUserId", "BuildingId")
    WHERE "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_BM_OperatorBuilding_BuildingId"
    ON "BuildingManagementOperatorBuildingGrants" ("BuildingId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // EF executes migration SQL transactionally on PostgreSQL.
        // ACCESS EXCLUSIVE prevents concurrent insert between check/drop.
        migrationBuilder.Sql("""
LOCK TABLE "BuildingManagementOperatorBuildingGrants"
    IN ACCESS EXCLUSIVE MODE;

DO $grant_rollback$
BEGIN
    IF EXISTS (
        SELECT 1 FROM "BuildingManagementOperatorBuildingGrants" LIMIT 1
    ) THEN
        RAISE EXCEPTION
            'Cannot roll back operator-building grants: history exists';
    END IF;
END;
$grant_rollback$;

DROP TABLE "BuildingManagementOperatorBuildingGrants";
            """);
    }
}
