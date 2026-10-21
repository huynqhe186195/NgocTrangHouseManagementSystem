-- Run only against the dedicated database ngoctrang_integration_test.
-- This SQL does NOT run against the application's database.
-- Treat existing naive timestamps as UTC; suitable for the fresh test DB created for Phase 2E.2.
\set ON_ERROR_STOP on
BEGIN;
DO $guard$
BEGIN
    IF current_database() <> 'ngoctrang_integration_test' THEN
        RAISE EXCEPTION 'SAFETY STOP: database is %, expected ngoctrang_integration_test', current_database();
    END IF;
END
$guard$;

ALTER TABLE public."BuildingManagementContractRenewalHolds"
    ALTER COLUMN "RequestedAt" TYPE timestamp with time zone USING "RequestedAt" AT TIME ZONE 'UTC',
    ALTER COLUMN "ExpiresAt"   TYPE timestamp with time zone USING "ExpiresAt" AT TIME ZONE 'UTC';

ALTER TABLE public."BuildingManagementRoomReservations"
    ALTER COLUMN "ReservedAt"  TYPE timestamp with time zone USING "ReservedAt" AT TIME ZONE 'UTC',
    ALTER COLUMN "CancelledAt" TYPE timestamp with time zone USING "CancelledAt" AT TIME ZONE 'UTC';

ALTER TABLE public."BuildingManagementPayments"
    ALTER COLUMN "PaidAt" TYPE timestamp with time zone USING "PaidAt" AT TIME ZONE 'UTC';

ALTER TABLE public."BuildingManagementDepositSettlements"
    ALTER COLUMN "CompletedAt" TYPE timestamp with time zone USING "CompletedAt" AT TIME ZONE 'UTC';

COMMIT;
