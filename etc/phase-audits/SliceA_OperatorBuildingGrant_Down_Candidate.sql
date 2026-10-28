-- SLICE A / OFFLINE CANDIDATE / DO NOT APPLY TO PRODUCTION
-- Caller MUST be in ONE explicitly controlled transaction.
-- Historical grants (including revoked grants) must NEVER be dropped.
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
DROP FUNCTION "BM_GuardOperatorGrantHistory"();
