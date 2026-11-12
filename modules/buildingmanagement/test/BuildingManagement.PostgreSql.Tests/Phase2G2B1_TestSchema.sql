-- Phase 2G.2B.1: TEST-DATABASE ONLY schema augmentation.
-- Do not run against application, staging or production databases.
-- The existing PostgreSQL integration test harness uses EnsureCreated(),
-- which does not update tables in an already-existing test database.
-- Mirrors migration 20261023103354_Phase2G2B1_AddMeterReplacements.
-- Intentionally DOES NOT change __EFMigrationsHistory; this is NOT a
-- replacement for normal EF migrations on a migrations-managed database.
--
-- Run only after verifying the selected database is ngoctrang_integration_test.
-- Entire operation rolls back on any error.
BEGIN;

DO $$
BEGIN
    IF current_database() <> 'ngoctrang_integration_test' THEN
        RAISE EXCEPTION
            'Safety stop: expected ngoctrang_integration_test, got %',
            current_database();
    END IF;
    IF to_regclass('public."BuildingManagementUtilityMeters"') IS NULL
       OR to_regclass('public."BuildingManagementRooms"') IS NULL THEN
        RAISE EXCEPTION
            'Safety stop: expected UtilityMeters and Rooms tables in public schema.';
    END IF;
    IF to_regclass('public."BuildingManagementMeterReplacements"') IS NOT NULL THEN
        RAISE EXCEPTION
            'Safety stop: BuildingManagementMeterReplacements already exists; review schema before applying.';
    END IF;
END
$$;

CREATE TABLE public."BuildingManagementMeterReplacements"
(
    "Id" uuid NOT NULL,
    "OldMeterId" uuid NOT NULL,
    "NewMeterId" uuid NOT NULL,
    "RoomId" uuid NOT NULL,
    "UtilityType" integer NOT NULL,
    "ReplacementDate" date NOT NULL,
    "OldPreviousReading" numeric(18,3) NOT NULL,
    "OldFinalReading" numeric(18,3) NOT NULL,
    "NewInitialReading" numeric(18,3) NOT NULL,
    "Reason" character varying(500) NOT NULL,
    "Notes" character varying(500),
    "ExtraProperties" text NOT NULL,
    "ConcurrencyStamp" character varying(40) NOT NULL,
    "CreationTime" timestamp with time zone NOT NULL,
    "CreatorId" uuid,
    "LastModificationTime" timestamp with time zone,
    "LastModifierId" uuid,
    CONSTRAINT "PK_BuildingManagementMeterReplacements" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BM_MeterReplacements_NewMeter"
        FOREIGN KEY ("NewMeterId")
        REFERENCES public."BuildingManagementUtilityMeters" ("Id")
        ON DELETE RESTRICT,
    CONSTRAINT "FK_BM_MeterReplacements_OldMeter"
        FOREIGN KEY ("OldMeterId")
        REFERENCES public."BuildingManagementUtilityMeters" ("Id")
        ON DELETE RESTRICT,
    CONSTRAINT "FK_BM_MeterReplacements_Room"
        FOREIGN KEY ("RoomId")
        REFERENCES public."BuildingManagementRooms" ("Id")
        ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_BuildingManagementMeterReplacements_NewMeterId"
    ON public."BuildingManagementMeterReplacements" ("NewMeterId");

CREATE UNIQUE INDEX "IX_BuildingManagementMeterReplacements_OldMeterId"
    ON public."BuildingManagementMeterReplacements" ("OldMeterId");

CREATE INDEX "IX_BuildingManagementMeterReplacements_RoomId_ReplacementDate"
    ON public."BuildingManagementMeterReplacements" ("RoomId", "ReplacementDate");

COMMIT;

-- Read-only verification after the transaction commits.
SELECT current_database() AS database_name,
       to_regclass('public."BuildingManagementMeterReplacements"') AS replacement_table;
