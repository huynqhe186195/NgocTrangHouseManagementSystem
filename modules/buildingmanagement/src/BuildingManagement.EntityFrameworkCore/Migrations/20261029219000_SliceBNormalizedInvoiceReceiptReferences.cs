using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only PostgreSQL invariant for Phase 2G.3 bank receipt replay.
/// InvoicePayment transaction references are unique after trimming and
/// case-folding, even when code bypasses the isolated staging HTTP adapter.
/// A migration with historical collisions FAILS rather than deleting or
/// rewriting financial evidence. Never run on production without approval.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029219000_SliceBNormalizedInvoiceReceiptReferences")]
public sealed class SliceBNormalizedInvoiceReceiptReferences : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- Fail before DDL on any existing ambiguous bank evidence.
            -- A case/whitespace variant must not become a second receipt.
            DO $normalized_reference_guard$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments"
                     WHERE "Purpose" = 3 AND "TransactionReference" IS NOT NULL
                       AND length(btrim("TransactionReference")) = 0
                ) THEN
                    RAISE EXCEPTION 'Blank invoice receipt reference requires manual reconciliation'
                        USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments"
                     WHERE "Purpose" = 3 AND "TransactionReference" IS NOT NULL
                     GROUP BY lower(btrim("TransactionReference"))
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Duplicate normalized invoice receipt references require manual reconciliation'
                        USING ERRCODE = '23505';
                END IF;
            END;
            $normalized_reference_guard$;

            CREATE UNIQUE INDEX "UX_BM_InvoicePayment_NormalizedBankReference"
                ON "BuildingManagementPayments"
                    (lower(btrim("TransactionReference")))
                WHERE "Purpose" = 3 AND "TransactionReference" IS NOT NULL;

            -- SQL guards exist alongside the old exact-reference index:
            -- this new index does not change other payment purposes.
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementPayments",
                       "BuildingManagementInvoicePaymentAllocationJournal"
                IN ACCESS EXCLUSIVE MODE;
            DO $normalized_reference_down$
            BEGIN
                IF EXISTS (
                    SELECT 1
                      FROM "BuildingManagementInvoicePaymentAllocationJournal"
                     LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Allocated invoice receipt history exists; refuse normalized-index rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $normalized_reference_down$;
            DROP INDEX "UX_BM_InvoicePayment_NormalizedBankReference";
            """);
    }
}
