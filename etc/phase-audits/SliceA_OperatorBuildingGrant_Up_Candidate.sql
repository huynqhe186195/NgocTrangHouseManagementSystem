-- SLICE A / OFFLINE CANDIDATE / DO NOT APPLY TO PRODUCTION
-- This is a human-review SQL candidate, NOT an EF migration.
-- Execute only inside the guarded, rolled-back PostgreSQL scratch-schema test.
-- Existing public application schema and EF migration history stay untouched.
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
        ("RevokedAtUtc" >= "GrantedAtUtc" AND
         "RevokedByUserId" IS NOT NULL AND
         "RevokedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid)
    )
);

-- A grant is reusable only after explicit revocation. An expired but
-- unrevoked record still occupies the unique slot; history is never reused.
CREATE UNIQUE INDEX "UX_BM_OperatorBuilding_Unrevoked"
    ON "BuildingManagementOperatorBuildingGrants"
    ("OperatorUserId", "BuildingId")
    WHERE "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_BM_OperatorBuilding_BuildingId"
    ON "BuildingManagementOperatorBuildingGrants" ("BuildingId");
