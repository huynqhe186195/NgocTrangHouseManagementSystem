START TRANSACTION;
CREATE TABLE "BuildingManagementInvoiceDraftRevisions" (
    "Id" uuid NOT NULL,
    "InvoiceId" uuid NOT NULL,
    "ArchivedRevision" integer NOT NULL,
    "ArchivedByUserId" uuid NOT NULL,
    "ArchivedAtUtc" timestamp with time zone NOT NULL,
    "Reason" character varying(500) NOT NULL,
    "EvidenceJson" text NOT NULL,
    "EvidenceSha256" character varying(64) NOT NULL,
    CONSTRAINT "PK_BuildingManagementInvoiceDraftRevisions" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_BM_InvoiceDraftRevision_Evidence" CHECK (length("EvidenceJson") BETWEEN 1 AND 1000000),
    CONSTRAINT "CK_BM_InvoiceDraftRevision_Hash" CHECK (length("EvidenceSha256") = 64 AND "EvidenceSha256" = lower("EvidenceSha256")),
    CONSTRAINT "CK_BM_InvoiceDraftRevision_Positive" CHECK ("ArchivedRevision" >= 1),
    CONSTRAINT "CK_BM_InvoiceDraftRevision_Reason" CHECK (length(trim("Reason")) BETWEEN 8 AND 500),
    CONSTRAINT "FK_BuildingManagementInvoiceDraftRevisions_BuildingManagementI~" FOREIGN KEY ("InvoiceId") REFERENCES "BuildingManagementInvoices" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "UX_BM_InvoiceDraftRevision_Invoice_Number" ON "BuildingManagementInvoiceDraftRevisions" ("InvoiceId", "ArchivedRevision");

ALTER TABLE "BuildingManagementInvoiceDraftRevisions"
ADD CONSTRAINT "CK_BM_InvoiceDraftRevision_HexSha256"
CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$');

CREATE FUNCTION "bm_block_invoice_draft_revision_mutation"()
RETURNS trigger
LANGUAGE plpgsql
AS $revision_guard$
BEGIN
    RAISE EXCEPTION 'Archived invoice draft revisions are immutable';
END;
$revision_guard$;

CREATE TRIGGER "TR_BM_InvoiceDraftRevision_ProtectRow"
BEFORE UPDATE OR DELETE
ON "BuildingManagementInvoiceDraftRevisions"
FOR EACH ROW
EXECUTE FUNCTION "bm_block_invoice_draft_revision_mutation"();

CREATE TRIGGER "TR_BM_InvoiceDraftRevision_ProtectTruncate"
BEFORE TRUNCATE
ON "BuildingManagementInvoiceDraftRevisions"
FOR EACH STATEMENT
EXECUTE FUNCTION "bm_block_invoice_draft_revision_mutation"();

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261025150750_Phase2G3A7_AddInvoiceDraftRevisionLedger', '10.0.9');

COMMIT;

