using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Phase2G3A7_AddInvoiceDraftRevisionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementInvoiceDraftRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedRevision = table.Column<int>(type: "integer", nullable: false),
                    ArchivedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementInvoiceDraftRevisions", x => x.Id);
                    table.CheckConstraint("CK_BM_InvoiceDraftRevision_Evidence", "length(\"EvidenceJson\") BETWEEN 1 AND 1000000");
                    table.CheckConstraint("CK_BM_InvoiceDraftRevision_Hash", "length(\"EvidenceSha256\") = 64 AND \"EvidenceSha256\" = lower(\"EvidenceSha256\")");
                    table.CheckConstraint("CK_BM_InvoiceDraftRevision_Positive", "\"ArchivedRevision\" >= 1");
                    table.CheckConstraint("CK_BM_InvoiceDraftRevision_Reason", "length(trim(\"Reason\")) BETWEEN 8 AND 500");
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoiceDraftRevisions_BuildingManagementI~",
                        column: x => x.InvoiceId,
                        principalTable: "BuildingManagementInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BM_InvoiceDraftRevision_Invoice_Number",
                table: "BuildingManagementInvoiceDraftRevisions",
                columns: new[] { "InvoiceId", "ArchivedRevision" },
                unique: true);

            // PostgreSQL-only defense in depth. The provider-neutral EF
            // check on length/lowercase does not reject non-hex characters.
            // This raw SQL is not represented in EF's model snapshot.
            migrationBuilder.Sql("""
                ALTER TABLE "BuildingManagementInvoiceDraftRevisions"
                ADD CONSTRAINT "CK_BM_InvoiceDraftRevision_HexSha256"
                CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$');
                """);

            // Append-only at the database boundary. PostgreSQL row triggers
            // do not fire on TRUNCATE, so guard it separately at statement level.
            // DB owners/superusers can still bypass triggers: operational
            // SQL permissions and backups remain mandatory protections.
            migrationBuilder.Sql("""
                CREATE FUNCTION "bm_block_invoice_draft_revision_mutation"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $revision_guard$
                BEGIN
                    RAISE EXCEPTION 'Archived invoice draft revisions are immutable';
                END;
                $revision_guard$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_BM_InvoiceDraftRevision_ProtectRow"
                BEFORE UPDATE OR DELETE
                ON "BuildingManagementInvoiceDraftRevisions"
                FOR EACH ROW
                EXECUTE FUNCTION "bm_block_invoice_draft_revision_mutation"();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_BM_InvoiceDraftRevision_ProtectTruncate"
                BEFORE TRUNCATE
                ON "BuildingManagementInvoiceDraftRevisions"
                FOR EACH STATEMENT
                EXECUTE FUNCTION "bm_block_invoice_draft_revision_mutation"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Prevent a concurrent INSERT from racing the empty-ledger check.
            // EF normally runs a migration in one database transaction. The
            // ACCESS EXCLUSIVE lock must be acquired in that SAME transaction
            // before checking emptiness and before any destructive DDL.
            migrationBuilder.Sql("""
                LOCK TABLE "BuildingManagementInvoiceDraftRevisions"
                IN ACCESS EXCLUSIVE MODE;
                """);

            // Block a rollback which would silently destroy archive history.
            // Do not run or deploy before dry-running on disposable PostgreSQL.
            migrationBuilder.Sql("""
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
                """);

            // Cleanup is reached only if no archived history exists.
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_BM_InvoiceDraftRevision_ProtectRow"
                ON "BuildingManagementInvoiceDraftRevisions";
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_BM_InvoiceDraftRevision_ProtectTruncate"
                ON "BuildingManagementInvoiceDraftRevisions";
                """);
            migrationBuilder.Sql("""
                DROP FUNCTION "bm_block_invoice_draft_revision_mutation"();
                """);
            migrationBuilder.DropTable(
                name: "BuildingManagementInvoiceDraftRevisions");
        }
    }
}
