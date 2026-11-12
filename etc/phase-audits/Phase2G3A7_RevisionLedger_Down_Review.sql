START TRANSACTION;
LOCK TABLE "BuildingManagementInvoiceDraftRevisions"
IN ACCESS EXCLUSIVE MODE;

DO $revision_rollback$
BEGIN
    IF EXISTS (
        SELECT 1 FROM "BuildingManagementInvoiceDraftRevisions" LIMIT 1
    ) THEN
        RAISE EXCEPTION
            'Cannot roll back draft revision ledger: archived history exists';
    END IF;
END;
$revision_rollback$;

DROP TRIGGER "TR_BM_InvoiceDraftRevision_ProtectRow"
ON "BuildingManagementInvoiceDraftRevisions";

DROP TRIGGER "TR_BM_InvoiceDraftRevision_ProtectTruncate"
ON "BuildingManagementInvoiceDraftRevisions";

DROP FUNCTION "bm_block_invoice_draft_revision_mutation"();

DROP TABLE "BuildingManagementInvoiceDraftRevisions";

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger';

COMMIT;

