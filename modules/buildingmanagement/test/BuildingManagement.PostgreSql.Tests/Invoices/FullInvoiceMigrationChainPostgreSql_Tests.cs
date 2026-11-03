using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Opt-in, disposable-database FULL migration-chain rehearsal.
/// NEVER targets existing application, migration, or integration DBs.
/// No EF EnsureCreated(), DbMigrator, migrations history tampering, or
/// data seeding. Requires explicitly enabled local test DB credential.
/// </summary>
public sealed class FullInvoiceMigrationChainPostgreSql_Tests
{
    private const string OptInVariable = "BM_ALLOW_FULL_MIGRATION_REHEARSAL";
    private const string OptInValue = "CREATE_AND_DROP_FRESH_LOCAL_TEST_DATABASE";
    private const string ExpectedLatestMigration =
        "20261103090000_Phase2HMidMonthFinancialJournal";

    [Fact]
    public async Task PGMIG01_Full_EF_Migrations_Succeed_In_Fresh_Disposable_Database()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != OptInValue)
            throw new InvalidOperationException(
                "Migration rehearsal is opt-in. Run only via " +
                "etc/scripts/test-full-invoice-migration-chain.ps1.");

        // Existing guard checks DB name ngoctrang_integration_test,
        // Host localhost/127.0.0.1 and port 5432 before any connection.
        var permittedConnection =
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var adminOptions = new NpgsqlConnectionStringBuilder(permittedConnection)
        {
            Pooling = false
        };

        // Unique name never belongs to a pre-existing application DB.
        var scratchName = "ngoctrang_migration_rehearsal_" +
            Guid.NewGuid().ToString("N");
        Assert.True(scratchName.Length < 63);

        await using var admin = new NpgsqlConnection(adminOptions.ConnectionString);
        await admin.OpenAsync();

        await using (var databaseCheck = new NpgsqlCommand(
            "SELECT current_database()", admin))
        {
            Assert.Equal(
                BuildingManagementPostgreSqlTestModule.DatabaseName,
                (string?)await databaseCheck.ExecuteScalarAsync());
        }

        // Stop before CREATE DATABASE if the test role lacks permission.
        await using (var permissionCheck = new NpgsqlCommand(
            "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user",
            admin))
        {
            if (!Convert.ToBoolean(await permissionCheck.ExecuteScalarAsync()))
                throw new InvalidOperationException(
                    "Safe stop: local PostgreSQL test role needs CREATEDB " +
                    "or SUPERUSER for disposable migration rehearsal. " +
                    "No test database was created.");
        }

        var created = false;
        var stage = "CREATE disposable local scratch database";
        try
        {
            // PostgreSQL CREATE DATABASE is not transactional; it creates
            // ONE distinct scratch DB with a random, tightly controlled name.
            await using (var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{scratchName}\" TEMPLATE template0",
                admin))
            {
                await create.ExecuteNonQueryAsync();
                created = true;
            }

            var scratchOptions = new NpgsqlConnectionStringBuilder(
                adminOptions.ConnectionString)
            {
                Database = scratchName,
                Pooling = false,
                CommandTimeout = 180
            };

            var dbOptions = new DbContextOptionsBuilder<BuildingManagementDbContext>()
                .UseNpgsql(scratchOptions.ConnectionString)
                .Options;

            stage = "Initialize EF migration chain in scratch database";
            // Run actual EF migrations from the initial module baseline,
            // rather than EnsureCreated() or replay of a single invoice DDL.
            await using (var db = new BuildingManagementDbContext(dbOptions))
            {
                stage = "Discover EF migrations in BuildingManagementDbContext";
                var migrations = db.Database.GetMigrations().ToArray();
                Assert.NotEmpty(migrations);
                Assert.Equal(ExpectedLatestMigration, migrations[^1]);

                stage = "Check initial EF migration history";
                var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                Assert.Empty(before);

                stage = "Apply full EF migration chain";
                await db.Database.MigrateAsync();

                stage = "Verify all EF migrations were applied";
                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                Assert.Equal(migrations, applied);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());

                stage = "Repeat EF migration for idempotency";
                // Repeat MigrateAsync to confirm an already fully migrated
                // database requires no new DDL or duplicate version inserts.
                await db.Database.MigrateAsync();
                var second = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                Assert.Equal(migrations, second);
            }

            stage = "Verify PostgreSQL invoice schema, indexes, constraints and extension";
            await using (var verification = new NpgsqlConnection(
                scratchOptions.ConnectionString))
            {
                await verification.OpenAsync();

                await using (var tableCount = new NpgsqlCommand(
                    @"SELECT COUNT(*) FROM pg_tables
                      WHERE schemaname = 'public'
                        AND tablename IN (
                            'BuildingManagementInvoices',
                            'BuildingManagementInvoiceItems',
                            'BuildingManagementInvoiceItemMeterSources')",
                    verification))
                {
                    Assert.Equal(3L,
                        Convert.ToInt64(await tableCount.ExecuteScalarAsync()));
                }

                await using (var uniqueIndexes = new NpgsqlCommand(
                    @"SELECT COUNT(*) FROM pg_indexes
                      WHERE schemaname = 'public'
                        AND indexname IN (
                            'UX_BM_Invoice_Contract_Period',
                            'UX_BM_Invoice_Room_Period',
                            'UX_BM_InvoiceItem_Invoice_Utility',
                            'UX_BM_InvoiceSource_Global',
                            'UX_BM_InvoiceSource_Item_Type_Source')
                        AND indexdef ILIKE 'CREATE UNIQUE INDEX%'",
                    verification))
                {
                    Assert.Equal(5L,
                        Convert.ToInt64(await uniqueIndexes.ExecuteScalarAsync()));
                }

                await using (var foreignKeys = new NpgsqlCommand(
                    @"SELECT COUNT(*) FROM pg_constraint fk
                      JOIN pg_class r ON r.oid = fk.conrelid
                      JOIN pg_namespace n ON n.oid = r.relnamespace
                      WHERE n.nspname = 'public'
                        AND fk.contype = 'f'
                        AND fk.confdeltype = 'r'
                        AND r.relname IN (
                            'BuildingManagementInvoices',
                            'BuildingManagementInvoiceItems',
                            'BuildingManagementInvoiceItemMeterSources')",
                    verification))
                {
                    Assert.Equal(6L,
                        Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));
                }

                // Confirm Phase 2G.4 refund SLA schema exists after the
                // complete migration chain, not merely old invoice tables.
                await using (var refundCalendar = new NpgsqlCommand(
                    """
                    SELECT COUNT(*)
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'BuildingManagementContractDepositRefundInstructions'
                      AND column_name = 'StatutoryHolidaysJson'
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await refundCalendar.ExecuteScalarAsync()));
                }

                await using (var refundSlaTrigger = new NpgsqlCommand(
                    """
                    SELECT COUNT(*) FROM pg_trigger
                    WHERE NOT tgisinternal
                      AND tgname = 'TR_BM_VerifyRefundHolidaySnapshotSla'
                      AND tgrelid =
                        '"BuildingManagementContractDepositRefundInstructions"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await refundSlaTrigger.ExecuteScalarAsync()));
                }

                // Phase 2G.5 archive parity must be a real SQL trigger in
                // a newly migrated database, not only a domain-level check.
                await using (var roomArchiveProof = new NpgsqlCommand(
                    """
                    SELECT COUNT(*) FROM pg_trigger
                    WHERE NOT tgisinternal
                      AND tgname = 'TR_BM_VerifyMoveOutDispositionArchive'
                      AND tgrelid =
                        '"BuildingManagementMoveOutRoomDispositions"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await roomArchiveProof.ExecuteScalarAsync()));
                }

                // Phase 2H must protect future commitments at the SQL
                // deferred commit boundary, not only through a controller.
                await using (var sourceDispositionGuard = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname = 'TR_BM_RoomChangeSourceDisposition_CommitGate'
                       AND tgrelid =
                         '"BuildingManagementRoomChangeCompletions"'::regclass
                       AND tgdeferrable AND tginitdeferred
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await sourceDispositionGuard.ExecuteScalarAsync()));
                }

                // H6 document authenticity-of-bytes: the canonical
                // handover digest must be checked by PostgreSQL on INSERT,
                // not only verified when the .NET domain reads it.
                await using (var handoverDigestProof = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname = 'TR_BM_RoomChangeHandover_DigestIntegrity'
                       AND tgrelid =
                         '"BuildingManagementRoomChangeHandoverEvidence"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await handoverDigestProof.ExecuteScalarAsync()));
                }

                // D01/D02 must have a real append-only SQL guard in
                // the completely migrated isolated PostgreSQL database.
                await using (var pricingDecisionTrigger = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname = 'TR_BM_RoomChangeDayPricingRule_Integrity'
                       AND tgrelid =
                         '"BuildingManagementRoomChangeDayPricingRuleCandidates"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await pricingDecisionTrigger.ExecuteScalarAsync()));
                }

                // D01/D03/D04/D06 append-only documentary archive must
                // survive the full EF migration chain on a fresh database.
                await using (var archiveTrigger = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname = 'TR_BM_2HArchive_ImmutableIntegrity'
                       AND tgrelid =
                         '"BuildingManagementRoomChangeEvidenceArchives"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await archiveTrigger.ExecuteScalarAsync()));
                }

                // Financial posting remains strongly scoped and append-only
                // even when accessed through raw SQL rather than EF/API.
                await using (var journalTrigger = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_trigger
                     WHERE NOT tgisinternal
                       AND tgname = 'TR_BM_2HFin_ImmutablePosting'
                       AND tgrelid =
                         '"BuildingManagementRoomChangeMidMonthFinancialPostings"'::regclass
                    """, verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await journalTrigger.ExecuteScalarAsync()));
                }

                await using (var extension = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM pg_extension WHERE extname = 'btree_gist'",
                    verification))
                {
                    Assert.Equal(1L,
                        Convert.ToInt64(await extension.ExecuteScalarAsync()));
                }
            }
        }
        catch (Exception ex)
        {
            var sqlState = ex is PostgresException pg
                ? " (PostgreSQL SQLSTATE " + pg.SqlState + ")"
                : string.Empty;
            throw new InvalidOperationException(
                "PGMIG01 failed at stage: " + stage + sqlState +
                ". Inspect the inner exception for the actual cause.", ex);
        }
        finally
        {
            if (created)
            {
                // Do not force-disconnect other sessions. All connections
                // created by this test use Pooling=false and are disposed
                // before this normal DROP DATABASE.
                await using var drop = new NpgsqlCommand(
                    $"DROP DATABASE \"{scratchName}\"", admin);
                try
                {
                    await drop.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Scratch DB cleanup FAILED. Inspect and remove ONLY " +
                        $"the test-created database {scratchName} manually " +
                        "after confirming it contains no business data.", ex);
                }
            }
        }

        // Scratch DB must no longer exist after successful teardown.
        await using var absenceCheck = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_database WHERE datname = @name", admin);
        absenceCheck.Parameters.AddWithValue("name", scratchName);
        Assert.Equal(0L, Convert.ToInt64(await absenceCheck.ExecuteScalarAsync()));
    }
}
