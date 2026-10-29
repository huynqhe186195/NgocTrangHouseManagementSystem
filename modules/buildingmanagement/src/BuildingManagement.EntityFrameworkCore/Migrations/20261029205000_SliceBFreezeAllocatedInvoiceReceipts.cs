using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace BuildingManagement.Migrations;
/// <summary>
/// Source-only: immutable allocation cannot be backed by mutable receipt cash.
/// Manual corrections require a future separate audited reversal process.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029205000_SliceBFreezeAllocatedInvoiceReceipts")]
public sealed class SliceBFreezeAllocatedInvoiceReceipts : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_GuardAllocatedInvoiceReceiptHistory"()
            RETURNS trigger LANGUAGE plpgsql AS $receipt_guard$
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    IF EXISTS (SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal" LIMIT 1)
                       OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1)
                    THEN
                        RAISE EXCEPTION 'Allocated or closed invoice receipt history cannot be truncated'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END IF;
                -- An immutable journal must not refer to a subsequently
                -- altered, soft-deleted, reassigned or erased Payment.
                IF TG_OP IN ('UPDATE','DELETE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal"
                     WHERE "PaymentId" = OLD."Id"
                ) THEN
                    RAISE EXCEPTION 'Journaled invoice payment is immutable'
                        USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'UPDATE' AND EXISTS (
                    SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal"
                     WHERE "PaymentId" = NEW."Id"
                ) THEN
                    RAISE EXCEPTION 'Invoice payment identifier is already journaled'
                        USING ERRCODE = '23514';
                END IF;
                -- Contract row lock serializes INSERTs and unjournaled
                -- payment mutation with immutable deposit-closeout approval.
                IF TG_OP = 'INSERT' THEN
                    IF NEW."Purpose" = 3 AND NEW."ContractId" IS NOT NULL THEN
                        PERFORM 1 FROM "BuildingManagementContracts"
                         WHERE "Id" = NEW."ContractId" FOR UPDATE;
                        IF EXISTS (
                            SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                             WHERE "ContractId" = NEW."ContractId"
                        ) THEN
                            RAISE EXCEPTION 'Closed contract cannot receive an invoice payment'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    IF OLD."Purpose" = 3 OR NEW."Purpose" = 3 THEN
                        PERFORM 1 FROM "BuildingManagementContracts"
                         WHERE "Id" IN (OLD."ContractId", NEW."ContractId")
                         ORDER BY "Id" FOR UPDATE;
                        IF EXISTS (
                            SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                             WHERE "ContractId" IN (OLD."ContractId", NEW."ContractId")
                        ) THEN
                            RAISE EXCEPTION 'Closed contract invoice receipt cannot be changed'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END IF;
                IF OLD."Purpose" = 3 AND OLD."ContractId" IS NOT NULL THEN
                    PERFORM 1 FROM "BuildingManagementContracts"
                     WHERE "Id" = OLD."ContractId" FOR UPDATE;
                    IF EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                         WHERE "ContractId" = OLD."ContractId"
                    ) THEN
                        RAISE EXCEPTION 'Closed contract invoice receipt cannot be deleted'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN OLD;
            END;
            $receipt_guard$;
            CREATE TRIGGER "TR_BM_FreezeAllocatedInvoicePayment"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementPayments"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardAllocatedInvoiceReceiptHistory"();
            CREATE TRIGGER "TR_BM_FreezeAllocatedInvoicePaymentTruncate"
                BEFORE TRUNCATE ON "BuildingManagementPayments"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardAllocatedInvoiceReceiptHistory"();
            """);
    }
    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementPayments",
                       "BuildingManagementInvoicePaymentAllocationJournal",
                       "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $freeze_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementInvoicePaymentAllocationJournal" LIMIT 1)
                   OR EXISTS (SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Allocated or approved receipt history exists; refuse downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $freeze_down$;
            DROP TRIGGER "TR_BM_FreezeAllocatedInvoicePayment" ON "BuildingManagementPayments";
            DROP TRIGGER "TR_BM_FreezeAllocatedInvoicePaymentTruncate" ON "BuildingManagementPayments";
            DROP FUNCTION "BM_GuardAllocatedInvoiceReceiptHistory"();
            """);
    }
}
