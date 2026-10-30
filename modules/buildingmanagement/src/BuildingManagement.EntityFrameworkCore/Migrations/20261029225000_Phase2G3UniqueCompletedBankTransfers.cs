using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// A completed bank transfer reference is evidence of one transfer, not a
/// reusable token per payment purpose. Existing exact-reference uniqueness
/// and invoice-only normalization cannot prevent a contract deposit from
/// being counted again as an invoice payment with different casing.
/// Reject historical collisions for manual reconciliation: never rewrite
/// or silently discard financial evidence.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029225000_Phase2G3UniqueCompletedBankTransfers")]
public sealed class Phase2G3UniqueCompletedBankTransfers : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            DO $bank_reference_audit$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments"
                     WHERE "Status" = 2 AND "Method" = 2
                       AND "TransactionReference" IS NOT NULL
                       AND length(btrim("TransactionReference")) = 0
                ) THEN
                    RAISE EXCEPTION
                      'Blank completed bank reference requires manual reconciliation'
                      USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments"
                     WHERE "Status" = 2 AND "Method" = 2
                       AND "TransactionReference" IS NOT NULL
                     GROUP BY lower(btrim("TransactionReference"))
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION
                      'Reused completed bank transfer requires manual reconciliation'
                      USING ERRCODE = '23505';
                END IF;
            END;
            $bank_reference_audit$;
            """);
        builder.Sql("""
            CREATE UNIQUE INDEX "UX_BM_CompletedBankTransfer_NormalizedReference"
              ON "BuildingManagementPayments"
                (lower(btrim("TransactionReference")))
             WHERE "Status" = 2 AND "Method" = 2
               AND "TransactionReference" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementPayments" IN ACCESS EXCLUSIVE MODE;
            DO $bank_reference_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementPayments"
                     WHERE "Status" = 2 AND "Method" = 2
                       AND "TransactionReference" IS NOT NULL
                ) THEN
                    RAISE EXCEPTION
                      'Completed bank history exists; uniqueness downgrade forbidden'
                      USING ERRCODE = '23514';
                END IF;
            END;
            $bank_reference_down$;
            """);
        builder.Sql("""
            DROP INDEX "UX_BM_CompletedBankTransfer_NormalizedReference";
            """);
    }
}
