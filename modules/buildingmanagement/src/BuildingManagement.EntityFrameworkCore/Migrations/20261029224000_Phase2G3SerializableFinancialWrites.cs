using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The monthly ledger has aggregate ceiling checks across independent inserts.
/// Under PostgreSQL REPEATABLE READ, locking the same issued invoice without
/// updating it does not refresh a transaction snapshot; two concurrent writers
/// can both evaluate a stale SUM. Require SERIALIZABLE (SSI) for both kinds of
/// balance-changing INSERT, including direct SQL paths outside the HTTP host.
/// This adds no official debt posting or production activation.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029224000_Phase2G3SerializableFinancialWrites")]
public sealed class Phase2G3SerializableFinancialWrites : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_RequireSerializableMonthlyFinance"()
            RETURNS trigger LANGUAGE plpgsql AS $serializable$
            BEGIN
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION
                      'Monthly finance writes require SERIALIZABLE transaction isolation'
                      USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $serializable$;
            """);
        builder.Sql("""
            CREATE TRIGGER "TR_BM_00_RequireSerializableMonthlyFinance"
            BEFORE INSERT
            ON "BuildingManagementMonthlyInvoicePaymentAllocation"
            FOR EACH ROW EXECUTE FUNCTION "BM_RequireSerializableMonthlyFinance"();
            """);
        builder.Sql("""
            CREATE TRIGGER "TR_BM_00_RequireSerializableMonthlyFinance"
            BEFORE INSERT
            ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
            FOR EACH ROW EXECUTE FUNCTION "BM_RequireSerializableMonthlyFinance"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMonthlyInvoicePaymentAllocation",
                       "BuildingManagementMonthlyInvoiceAdjustmentDecisions"
              IN ACCESS EXCLUSIVE MODE;
            DO $serializable_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementMonthlyInvoicePaymentAllocation" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM
                    "BuildingManagementMonthlyInvoiceAdjustmentDecisions" LIMIT 1)
                THEN
                    RAISE EXCEPTION
                      'Monthly financial history exists; isolation downgrade forbidden'
                      USING ERRCODE = '23514';
                END IF;
            END;
            $serializable_down$;
            """);
        builder.Sql("""
            DROP TRIGGER "TR_BM_00_RequireSerializableMonthlyFinance"
              ON "BuildingManagementMonthlyInvoiceAdjustmentDecisions";
            """);
        builder.Sql("""
            DROP TRIGGER "TR_BM_00_RequireSerializableMonthlyFinance"
              ON "BuildingManagementMonthlyInvoicePaymentAllocation";
            """);
        builder.Sql("""
            DROP FUNCTION "BM_RequireSerializableMonthlyFinance"();
            """);
    }
}
