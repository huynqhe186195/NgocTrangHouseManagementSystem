using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only. A signed deposit closeout freezes further invoice issuance.
/// Uses the same contract row as the deposit-closeout approval trigger to
/// serialize concurrent direct SQL writers through commit.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029210000_SliceBBlockIssuedInvoiceAfterCloseout")]
public sealed class SliceBBlockIssuedInvoiceAfterCloseout : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_BlockIssuedInvoiceAfterDepositCloseout"()
            RETURNS trigger LANGUAGE plpgsql AS $closed_invoice$
            BEGIN
                -- Approval INSERT locks this same contract row FOR UPDATE
                -- before checking the complete invoice/receipt ledger.
                -- In READ COMMITTED the second statement below sees the
                -- winning transaction's committed approval after the wait.
                PERFORM 1 FROM "BuildingManagementContracts"
                 WHERE "Id" = NEW."ContractId" FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'Issued invoice requires an existing contract'
                        USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = NEW."ContractId"
                ) THEN
                    RAISE EXCEPTION 'Closed contract cannot receive new issued invoices'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $closed_invoice$;

            CREATE TRIGGER "TR_BM_IssuedLedger_RejectAfterDepositCloseout"
                BEFORE INSERT ON "BuildingManagementIssuedInvoiceLedger"
                FOR EACH ROW EXECUTE FUNCTION "BM_BlockIssuedInvoiceAfterDepositCloseout"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementIssuedInvoiceLedger",
                       "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $closed_invoice_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed closeout exists; refuse invoice-freeze downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $closed_invoice_down$;
            DROP TRIGGER "TR_BM_IssuedLedger_RejectAfterDepositCloseout"
                ON "BuildingManagementIssuedInvoiceLedger";
            DROP FUNCTION "BM_BlockIssuedInvoiceAfterDepositCloseout"();
            """);
    }
}
