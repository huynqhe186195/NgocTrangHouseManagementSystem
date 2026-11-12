using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only safeguard against reusing a manually claimed bank refund
/// reference to approve a second contract's cash closeout. This does not
/// prove the external transfer occurred; only prevents duplicate evidence.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029211000_SliceBUniqueDepositRefundReferences")]
public sealed class SliceBUniqueDepositRefundReferences : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            -- One normalized bank transfer reference may attest to only
            -- ONE refund. Normalize case and outer whitespace, unlike an
            -- ordinary case-sensitive index. Pure forfeits have no refund.
            -- Existing duplicates fail the migration rather than passing
            -- silently as two independent, allegedly paid refunds.
            CREATE UNIQUE INDEX "UX_BM_DepositCloseout_RefundBankReference"
                ON "BuildingManagementContractDepositCloseoutApprovals"
                (lower(btrim("RefundTransactionReference")))
                WHERE "RefundTransactionReference" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $refund_reference_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed closeout exists; refuse refund-reference downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $refund_reference_down$;
            DROP INDEX "UX_BM_DepositCloseout_RefundBankReference";
            """);
    }
}
