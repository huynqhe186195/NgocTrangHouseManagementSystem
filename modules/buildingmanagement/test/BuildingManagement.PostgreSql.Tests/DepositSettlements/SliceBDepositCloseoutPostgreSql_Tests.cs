using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real deposit-closeout Up/Down SQL on a random transaction-local schema,
/// seeded with minimal predecessor relations. No application DB changes.
/// </summary>
public sealed class SliceBDepositCloseoutPostgreSql_Tests
{
    [Fact]
    public async Task SBDPDB01_Completed_Dispositions_Archive_Uniquely_And_Are_Immutable()
    {
        await ScratchAsync(async (db, tx, down) =>
        {
            var f = await SeedAsync(db, tx, completed: true);
            await ExecAsync(db, tx, ApprovalSql(f));
            Assert.Equal(1L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));

            await DeniedAsync(db, tx,
                ApprovalSql(f).Replace(
                    $"'{f.ApprovalId:D}'", $"'{Guid.NewGuid():D}'",
                    StringComparison.Ordinal), "23505");
            await DeniedAsync(db, tx, """
                UPDATE "BuildingManagementContractDepositCloseoutApprovals"
                SET "ApprovedRefundAmount" = 1
                """, "23514");
            await DeniedAsync(db, tx, """
                DELETE FROM "BuildingManagementContractDepositCloseoutApprovals"
                """, "23514");
            await DeniedAsync(db, tx, """
                TRUNCATE "BuildingManagementContractDepositCloseoutApprovals" CASCADE
                """, "23514");
            await DeniedAsync(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                SET "Amount" = 1 WHERE "Id" = '{f.RefundId:D}'
                """, "23514");
            await DeniedAsync(db, tx, $"""
                DELETE FROM "BuildingManagementDepositSettlements"
                WHERE "Id" = '{f.RefundId:D}'
                """, "23514");
            await DeniedAsync(db, tx, $"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  ("Id","Type","Status","SourceContractId","TargetContractId",
                   "Amount","CompletedAt","Notes")
                VALUES ('{Guid.NewGuid():D}',2,2,'{f.ContractId:D}',NULL,
                        1,'2026-10-29T02:30:00Z','Unauthorized extra forfeit')
                """, "23514");
            await DeniedAsync(db, tx, down, "23514");
            Assert.Equal(1L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));
        });
    }

    [Fact]
    public async Task SBDPDB02_Pending_Refund_Or_CrossContract_Disbursement_Cannot_Approve()
    {
        await ScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedAsync(db, tx, completed: false);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");
            await ExecAsync(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                SET "Status" = 2 WHERE "Id" = '{f.RefundId:D}'
                """);
            await DeniedAsync(db, tx,
                ApprovalSql(f).Replace(
                    $"'{f.ContractId:D}'", $"'{Guid.NewGuid():D}'",
                    StringComparison.Ordinal), "23514");
            Assert.Equal(0L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));
        });
    }

    [Fact]
    public async Task SBDPDB04_Unpaid_Or_Partially_Paid_Invoice_Blocks_Closeout()
    {
        await ScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedAsync(db, tx, completed: true);
            await ExecAsync(db, tx, $"""
                DELETE FROM "BuildingManagementInvoicePaymentAllocationJournal"
                 WHERE "IssuedInvoiceId" = '{f.InvoiceId:D}';
                """);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");

            // A partial receipt is also insufficient, even if every
            // deposit Refund/Forfeit settlement is already completed.
            await ExecAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoicePaymentAllocationJournal"
                    ("Id","PaymentId","IssuedInvoiceId","ContractId","Amount")
                VALUES ('{Guid.NewGuid():D}','{f.PaymentId:D}',
                        '{f.InvoiceId:D}','{f.ContractId:D}',999999);
                """);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");

            // Exact invoice total permits approval, subject to all other
            // independent deposit and legal signoff checks.
            await ExecAsync(db, tx, """
                UPDATE "BuildingManagementInvoicePaymentAllocationJournal"
                   SET "Amount" = 3000000
                """);
            await ExecAsync(db, tx, ApprovalSql(f));
            Assert.Equal(1L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementContractDepositCloseoutApprovals\""));
        });
    }

    [Fact]
    public async Task SBDPDB06_Drifted_Or_Unjournaled_Receipt_Blocks_Closeout()
    {
        await ScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedAsync(db, tx, completed: true);
            await ExecAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Status" = 1
                WHERE "Id" = '{f.PaymentId:D}';
                """);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");
            await ExecAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Status" = 2
                WHERE "Id" = '{f.PaymentId:D}';
                """);
            await ExecAsync(db, tx, $"""
                INSERT INTO "BuildingManagementPayments"
                    ("Id","InvoiceId","ContractId","Purpose","Status","Amount","IsDeleted")
                VALUES ('{Guid.NewGuid():D}','{f.InvoiceId:D}',
                        '{f.ContractId:D}',3,2,100000,FALSE);
                """);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");
        });
    }

    [Fact]
    public async Task SBDPDB05_Contract_Deposit_Amount_Cannot_Be_Spoofed()
    {
        await ScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedAsync(db, tx, completed: true);
            await ExecAsync(db, tx, $"""
                UPDATE "BuildingManagementContracts"
                   SET "DepositAmount" = 2000000
                 WHERE "Id" = '{f.ContractId:D}';
                """);
            await DeniedAsync(db, tx, ApprovalSql(f), "23514");
        });
    }

    [Fact]
    public async Task SBDPDB03_Empty_Down_Removes_Only_Approval_Schema()
    {
        await ScratchAsync(async (db, tx, down) =>
        {
            Assert.Equal(3L, await ScalarAsync(db, tx, """
                SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                  AND (tgrelid =
                     '"BuildingManagementContractDepositCloseoutApprovals"'::regclass
                    OR (tgrelid =
                     '"BuildingManagementDepositSettlements"'::regclass
                         AND tgname='TR_BM_DepositCloseout_ProtectSettlements'))
                """));
            await ExecAsync(db, tx, down);
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT to_regclass('"BuildingManagementContractDepositCloseoutApprovals"') IS NULL
                """));
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT to_regclass('"BuildingManagementDepositSettlements"') IS NOT NULL
                """));
        });
    }

    private sealed record Fixture(
        Guid ApprovalId, Guid ContractId, Guid RequestId,
        Guid InvoiceId, Guid RoomId, Guid BuildingId,
        Guid RefundId, Guid ForfeitId, Guid PaymentId);

    private static async Task<Fixture> SeedAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, bool completed)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync(db, tx, $"""
            INSERT INTO "BuildingManagementContracts" ("Id","DepositAmount")
                VALUES ('{f.ContractId:D}',1000000);
            INSERT INTO "BuildingManagementMoveOutRequests"
                ("Id","ContractId","RoomId","Status")
                VALUES ('{f.RequestId:D}','{f.ContractId:D}','{f.RoomId:D}',1);
            INSERT INTO "BuildingManagementIssuedInvoiceLedger"
                ("Id","ContractId","RoomId","MoveOutRequestId",
                 "BuildingId","IssuedAtUtc")
                VALUES ('{f.InvoiceId:D}','{f.ContractId:D}','{f.RoomId:D}',
                        '{f.RequestId:D}','{f.BuildingId:D}',
                        '2026-10-29T01:00:00Z');
            INSERT INTO "BuildingManagementPayments"
                ("Id","InvoiceId","ContractId","Purpose","Status","Amount","IsDeleted")
                VALUES ('{f.PaymentId:D}','{f.InvoiceId:D}','{f.ContractId:D}',
                        3,2,3000000,FALSE);
            INSERT INTO "BuildingManagementInvoicePaymentAllocationJournal"
                ("Id","PaymentId","IssuedInvoiceId","ContractId","Amount")
                VALUES ('{Guid.NewGuid():D}','{f.PaymentId:D}',
                        '{f.InvoiceId:D}','{f.ContractId:D}',3000000);
            INSERT INTO "BuildingManagementDepositSettlements"
                ("Id","Type","Status","SourceContractId","TargetContractId",
                 "Amount","CompletedAt","Notes")
                VALUES ('{Guid.NewGuid():D}',3,2,NULL,'{f.ContractId:D}',
                        1000000,'2026-10-29T01:15:00Z','Confirmed deposit');
            INSERT INTO "BuildingManagementDepositSettlements"
                ("Id","Type","Status","SourceContractId","TargetContractId",
                 "Amount","CompletedAt","Notes")
                VALUES ('{f.RefundId:D}',1,{(completed ? 2 : 1)},
                        '{f.ContractId:D}',NULL,800000,
                        '2026-10-29T02:00:00Z','BANK REFUND-TXN-0001');
            INSERT INTO "BuildingManagementDepositSettlements"
                ("Id","Type","Status","SourceContractId","TargetContractId",
                 "Amount","CompletedAt","Notes")
                VALUES ('{f.ForfeitId:D}',2,2,'{f.ContractId:D}',NULL,200000,
                        '2026-10-29T02:01:00Z','Documented floor tile damage');
            """);
        return f;
    }

    private static string ApprovalSql(Fixture f) => $"""
        INSERT INTO "BuildingManagementContractDepositCloseoutApprovals"
        ("Id","ContractId","MoveOutRequestId","IssuedInvoiceId",
         "BuildingId","ApprovedByUserId","ApprovedAtUtc",
         "VerifiedHeldAmount","ApprovedForfeitAmount","ApprovedRefundAmount",
         "RefundSettlementId","ForfeitSettlementId","RefundTransactionReference",
         "RefundPaidAtUtc","DamageReason","DamageEvidenceReference",
         "InspectionEvidenceReference","HandoverEvidenceReference",
         "ExplicitDepositPolicyReference","ApprovalJson","ApprovalSha256")
        VALUES ('{f.ApprovalId:D}','{f.ContractId:D}','{f.RequestId:D}',
                '{f.InvoiceId:D}','{f.BuildingId:D}','{Guid.NewGuid():D}',
                '2026-10-29T03:00:00Z',1000000,200000,800000,
                '{f.RefundId:D}','{f.ForfeitId:D}','REFUND-TXN-0001',
                '2026-10-29T02:00:00Z','Documented floor tile damage',
                'INSPECTION-PHOTO-0001','SIGNED-INSPECTION-0001',
                'SIGNED-HANDOVER-0001','APPROVED-DEPOSIT-POLICY',
                '{string.Concat((char)123,(char)125)}','{new string('a',64)}');
        """;

    private static string MigrationSql(bool down)
    {
        var m = new SliceBContractDepositCloseoutApprovals();
        var ops = down ? m.DownOperations : m.UpOperations;
        var op = Assert.Single(ops.OfType<SqlOperation>());
        Assert.Single(ops);
        Assert.Contains(down ? "refuse rollback" : "TR_BM_DepositCloseout_Immutable",
            op.Sql, StringComparison.Ordinal);
        return op.Sql;
    }

    private static async Task ScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> test)
    {
        var up = MigrationSql(false);
        var down = MigrationSql(true);
        var builder = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        { Pooling = false, CommandTimeout = 30 };
        await using var db = new NpgsqlConnection(builder.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_deposit_approval_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await ExecAsync(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await ExecAsync(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await ExecAsync(db, tx, "SET LOCAL lock_timeout = '3s'");
                await ExecAsync(db, tx, "SET LOCAL statement_timeout = '20s'");
                await ExecAsync(db, tx, """
                    CREATE TABLE "BuildingManagementContracts" (
                        "Id" uuid PRIMARY KEY,
                        "DepositAmount" numeric(18,0) NOT NULL);
                    CREATE TABLE "BuildingManagementMoveOutRequests" (
                        "Id" uuid PRIMARY KEY,
                        "ContractId" uuid NOT NULL,
                        "RoomId" uuid NOT NULL,
                        "Status" integer NOT NULL);
                    CREATE TABLE "BuildingManagementIssuedInvoiceLedger" (
                        "Id" uuid PRIMARY KEY,
                        "ContractId" uuid NOT NULL,
                        "RoomId" uuid NOT NULL,
                        "MoveOutRequestId" uuid NOT NULL,
                        "BuildingId" uuid NOT NULL,
                        "IssuedAtUtc" timestamptz NOT NULL,
                        "TotalAmount" numeric(18,0) NOT NULL DEFAULT 3000000);
                    CREATE TABLE "BuildingManagementPayments" (
                        "Id" uuid PRIMARY KEY,
                        "InvoiceId" uuid NULL,
                        "ContractId" uuid NULL,
                        "Purpose" integer NOT NULL,
                        "Status" integer NOT NULL,
                        "Amount" numeric(18,0) NOT NULL,
                        "IsDeleted" boolean NOT NULL);
                    CREATE TABLE "BuildingManagementInvoicePaymentAllocationJournal" (
                        "Id" uuid PRIMARY KEY,
                        "PaymentId" uuid NOT NULL,
                        "IssuedInvoiceId" uuid NOT NULL,
                        "ContractId" uuid NOT NULL,
                        "Amount" numeric(18,0) NOT NULL);
                    CREATE TABLE "BuildingManagementDepositSettlements" (
                        "Id" uuid PRIMARY KEY,
                        "Type" integer NOT NULL,
                        "Status" integer NOT NULL,
                        "SourceContractId" uuid NULL,
                        "TargetContractId" uuid NULL,
                        "Amount" numeric(18,0) NOT NULL,
                        "CompletedAt" timestamptz NULL,
                        "Notes" varchar(500) NULL);
                    """);
                await ExecAsync(db, tx, up);
                await test(db, tx, down);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await ScalarAsync(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task DeniedAsync(
        NpgsqlConnection db, NpgsqlTransaction tx,
        string sql, string expectedState)
    {
        const string savepoint = "deposit_approval_probe";
        await ExecAsync(db, tx, $"SAVEPOINT {savepoint}");
        string? actual = null;
        try { await ExecAsync(db, tx, sql); }
        catch (PostgresException e) { actual = e.SqlState; }
        finally
        {
            await ExecAsync(db, tx, $"ROLLBACK TO SAVEPOINT {savepoint}");
            await ExecAsync(db, tx, $"RELEASE SAVEPOINT {savepoint}");
        }
        Assert.Equal(expectedState, actual);
    }

    private static async Task ExecAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
