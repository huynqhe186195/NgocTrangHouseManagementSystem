using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real EF Core migration upgrade/rollback/upgrade against a database
/// created and dropped by this test, not the existing integration database.
/// Requires explicit scratch database opt-in; no production connection.
/// </summary>
public sealed class SliceBFullFinancialMigrationChainPostgreSql_Tests
{
    private const string OptIn = "BM_PG_SLICE_A_FULL_CHAIN_OPT_IN";
    private const string Accepted = "CREATE_AND_DROP_RANDOM_LOCAL_SLICE_A_DATABASE";
    private const string LastSliceAMigration =
        "20261028190000_SliceAOwnerRecoveryApprovals";
    private const string LastSliceBMigration =
        "20261029218000_SliceBLockBothDepositSettlementContracts";

    [Fact]
    public async Task SBCHAIN01_Full_Runtime_Migrations_Upgrade_EmptyDown_And_Reupgrade()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != Accepted)
            throw new InvalidOperationException(
                "Explicit disposable PostgreSQL full-chain opt-in required.");
        var approved = BuildingManagementPostgreSqlTestModule
            .RequireSafeConnectionString();
        var adminOptions = new NpgsqlConnectionStringBuilder(approved)
        {
            Pooling = false, CommandTimeout = 90
        };
        await using var admin = new NpgsqlConnection(adminOptions.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, admin.Database);
        await using (var privilege = new NpgsqlCommand(
            "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname=current_user",
            admin))
            Assert.True((bool)(await privilege.ExecuteScalarAsync() ?? false));

        var dbName = "bm_sliceb_rehearsal_" + Guid.NewGuid().ToString("N");
        Assert.Matches(@"^bm_sliceb_rehearsal_[0-9a-f]{32}$", dbName);
        var created = false;
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE \"{dbName}\" TEMPLATE template0");
            created = true;
            var scoped = new NpgsqlConnectionStringBuilder(adminOptions.ConnectionString)
            {
                Database = dbName, Pooling = false, CommandTimeout = 120
            }.ConnectionString;
            // Capture only the failed command text from disposable SQL.
            string lastFailedCommand = string.Empty;
            var options = new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(scoped)
                .LogTo(message =>
                {
                    if (message.Contains("Failed executing DbCommand",
                        StringComparison.OrdinalIgnoreCase))
                        lastFailedCommand = message;
                }, LogLevel.Error).Options;
            await using var db = new BuildingManagementDbContext(options);

            var all = db.Database.GetMigrations().ToArray();
            Assert.Equal(1, all.Count(x => x == LastSliceBMigration));
            Assert.True(Array.IndexOf(all, LastSliceBMigration) >
                Array.IndexOf(all, LastSliceAMigration));

            try
            {
                await db.Database.MigrateAsync();
            }
            catch (PostgresException ex)
            {
                var commandPreview = lastFailedCommand.Length > 5500
                    ? lastFailedCommand[^5500..] : lastFailedCommand;
                throw new InvalidOperationException(
                    $"Full-chain migration failed SQLSTATE {ex.SqlState}, " +
                    $"position {ex.Position}; statement: {commandPreview}", ex);
            }
            Assert.Equal(all,
                (await db.Database.GetAppliedMigrationsAsync()).ToArray());

            await using (var inspect = new NpgsqlConnection(scoped))
            {
                await inspect.OpenAsync();
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementIssuedInvoiceLedger\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementInvoiceAllocationCandidateBalances\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    """
                    SELECT count(*) FROM pg_constraint
                    WHERE conname = 'FK_BM_Allocation_ActualIssuedInvoice'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    """
                    SELECT count(*) FROM pg_constraint
                    WHERE conname = 'CK_BM_IssuedLedger_JsonMatchesColumns'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementContractDepositCloseoutApprovals\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT count(*) FROM pg_constraint WHERE conname = 'FK_BM_DepositCloseout_RefundSettlement'"));
                Assert.Equal(2L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementPayments"'::regclass
                     AND tgname LIKE 'TR_BM_FreezeAllocated%'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementIssuedInvoiceLedger"'::regclass
                     AND tgname = 'TR_BM_IssuedLedger_RejectAfterDepositCloseout'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_indexes
                     WHERE indexname = 'UX_BM_DepositCloseout_RefundBankReference'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementContracts"'::regclass
                     AND tgname = 'TR_BM_ClosedContract_FreezeSource'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementMoveOutRequests"'::regclass
                     AND tgname = 'TR_BM_ClosedMoveOut_FreezeSource'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementContractDepositCloseoutApprovals"'::regclass
                     AND tgname = 'TR_BM_DepositCloseout_RequireCompletedAtCommit'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementContractDepositCloseoutApprovals"'::regclass
                     AND tgname = 'TR_BM_DepositCloseout_RequireChecklistAtCommit'
                    """));
                Assert.Equal(2L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementMoveOutChecklistTasks"'::regclass
                     AND tgname LIKE 'TR_BM_ClosedChecklist%'
                    """));
                Assert.Equal(2L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementContractTenants"'::regclass
                     AND tgname LIKE 'TR_BM_SignedContractTenant_%'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementPayments"'::regclass
                     AND tgname = 'TR_BM_ClosedDepositReceipt_SourceFreeze'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgrelid='"BuildingManagementContractDepositCloseoutApprovals"'::regclass
                     AND tgname = 'TR_BM_DepositCloseout_01_CheckDepositSources'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT (position('ORDER BY c."Id" FOR UPDATE'
                        IN pg_get_functiondef('"BM_GuardFinalizedDepositSettlements"()'::regprocedure)) > 0)::integer
                    """));
                Assert.Equal(3L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgname IN ('TR_BM_SignedContract_Extras',
                                    'TR_BM_SignedMoveOut_Extras',
                                    'TR_BM_SignedChecklist_Extras')
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementOfficialInvoiceReceivables\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementOfficialInvoiceReceiptReviews\"') IS NOT NULL"));
                Assert.Equal(2L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname IN ('TR_BM_OfficialReceivableImmutable',
                                      'TR_BM_OfficialReceiptReviewImmutable')
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_indexes WHERE
                    indexname = 'UX_BM_OfficialReceipt_Payment'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementContractDepositSettlementDecisions\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal AND
                       tgname = 'TR_BM_DepositDecision_Immutable'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal AND
                       tgname = 'TR_BM_00_RequireApprovedDepositDecision'
                       AND tgrelid =
                           '"BuildingManagementContractDepositCloseoutApprovals"'::regclass
                    """));
                Assert.Equal(3L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_indexes WHERE
                      indexname IN ('UX_BM_DepositDecision_Contract',
                                    'UX_BM_DepositDecision_MoveOut',
                                    'UX_BM_DepositDecision_FinalInvoice')
                    """));
                // A completed bank transfer can be allocated only once
                // across deposits and invoices, ignoring ref casing/spaces.
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_indexes
                     WHERE indexname =
                       'UX_BM_CompletedBankTransfer_NormalizedReference'
                    """));
                Assert.Equal(1L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_indexes
                     WHERE indexname =
                       'UX_BM_CompletedBankTransfer_NormalizedReference'
                       AND indexdef LIKE '%lower(btrim%'
                    """));
                // Both aggregate-ceiling writers must enforce SSI even
                // for direct SQL callers that bypass the staging HTTP gate.
                Assert.Equal(2L, await ScalarAsync(inspect, """
                    SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                     AND tgname = 'TR_BM_00_RequireSerializableMonthlyFinance'
                     AND tgrelid IN (
                       '"BuildingManagementMonthlyInvoicePaymentAllocation"'::regclass,
                       '"BuildingManagementMonthlyInvoiceAdjustmentDecisions"'::regclass)
                    """));
                await using (var wrongIsolation = new NpgsqlCommand(
                    "INSERT INTO \"BuildingManagementMonthlyInvoiceAdjustmentDecisions\" DEFAULT VALUES",
                    inspect))
                {
                    var failure = await Assert.ThrowsAsync<PostgresException>(
                        async () => { await wrongIsolation.ExecuteNonQueryAsync(); });
                    Assert.Equal("23514", failure.SqlState);
                    Assert.Contains("SERIALIZABLE", failure.MessageText,
                        StringComparison.Ordinal);
                }
            }

            // All financial sources are empty: reversible source-only
            // migrations must remove only their OWN schema. Owner grants,
            // invoice drafts and legacy application tables remain intact.
            await db.Database.MigrateAsync(LastSliceAMigration);
            var expected = all.Take(Array.IndexOf(all,
                LastSliceAMigration) + 1).ToArray();
            Assert.Equal(expected,
                (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            await using (var inspect = new NpgsqlConnection(scoped))
            {
                await inspect.OpenAsync();
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementIssuedInvoiceLedger\"') IS NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementOfficialInvoiceReceivables\"') IS NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementContractDepositSettlementDecisions\"') IS NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementContractDepositCloseoutApprovals\"') IS NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementInvoiceAllocationCandidateBalances\"') IS NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementInvoices\"') IS NOT NULL"));
                Assert.Equal(1L, await ScalarAsync(inspect,
                    "SELECT to_regclass('\"BuildingManagementOperatorBuildingGrants\"') IS NOT NULL"));
            }

            await db.Database.MigrateAsync();
            Assert.Equal(all,
                (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        }
        finally
        {
            if (created)
                await ExecuteAsync(admin, $"DROP DATABASE \"{dbName}\"");
        }
        Assert.Equal(0L, await ScalarAsync(admin,
            $"SELECT count(*) FROM pg_database WHERE datname = '{dbName}'"));
    }

    private static async Task ExecuteAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
