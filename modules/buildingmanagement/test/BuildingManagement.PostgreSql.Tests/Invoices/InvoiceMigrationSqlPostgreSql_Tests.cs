using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Executes the EXACT reviewed EF-generated invoice migration DDL against
/// a transaction-local, randomly named PostgreSQL schema. It ignores only
/// BEGIN/COMMIT and __EFMigrationsHistory bookkeeping, then ALWAYS rolls
/// back. It does not run the ABP TestModule or EnsureCreated().
/// </summary>
public sealed class InvoiceMigrationSqlPostgreSql_Tests
{
    private static readonly string[] Tables =
    {
        "BuildingManagementInvoices",
        "BuildingManagementInvoiceItems",
        "BuildingManagementInvoiceItemMeterSources"
    };

    private static readonly string[] ExpectedIndexes =
    {
        "IX_BuildingManagementInvoiceItemMeterSources_UtilityMeterId",
        "UX_BM_InvoiceSource_Global",
        "UX_BM_InvoiceSource_Item_Type_Source",
        "IX_BuildingManagementInvoiceItems_UtilityRateId",
        "UX_BM_InvoiceItem_Invoice_Utility",
        "UX_BM_Invoice_Contract_Period",
        "UX_BM_Invoice_Room_Period"
    };

    private static List<string> LoadReviewedDdlOnly()
    {
        const string resource = "Phase2G3A4_InvoiceDraftSchema_Review.sql";
        using var stream = typeof(InvoiceMigrationSqlPostgreSql_Tests)
            .Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "Missing reviewed EF invoice SQL embedded resource.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd().TrimStart('\uFEFF');
        var statements = sql.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal("START TRANSACTION", statements[0]);
        Assert.Equal("COMMIT", statements[^1]);

        var history = statements
            .Where(x => x.StartsWith("INSERT INTO \"__EFMigrationsHistory\"",
                StringComparison.Ordinal)).ToArray();
        Assert.Single(history);
        Assert.Contains(
            "20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots",
            history[0], StringComparison.Ordinal);

        var ddl = statements.Skip(1).SkipLast(1)
            .Where(x => !x.StartsWith("INSERT INTO \"__EFMigrationsHistory\"",
                StringComparison.Ordinal)).ToList();

        var createdTables = ddl
            .Where(x => x.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
            .Select(x => Regex.Match(x,
                "^CREATE TABLE \"([^\"]+)\" \\(", RegexOptions.CultureInvariant)
                .Groups[1].Value).ToArray();
        Assert.Equal(Tables, createdTables);

        var indexNames = ddl
            .Where(x => x.StartsWith("CREATE INDEX ", StringComparison.Ordinal) ||
                        x.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal))
            .Select(x => Regex.Match(x,
                "^CREATE(?: UNIQUE)? INDEX \"([^\"]+)\" ON",
                RegexOptions.CultureInvariant).Groups[1].Value).ToArray();
        Assert.Equal(ExpectedIndexes, indexNames);

        // No other operation is allowed in this migration rehearsal.
        Assert.Equal(10, ddl.Count);
        Assert.DoesNotContain(ddl, x =>
            !x.StartsWith("CREATE TABLE ", StringComparison.Ordinal) &&
            !x.StartsWith("CREATE INDEX ", StringComparison.Ordinal) &&
            !x.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal));

        Assert.All(ddl, x => Assert.DoesNotContain(
            "public.", x, StringComparison.OrdinalIgnoreCase));
        return ddl;
    }

    [Fact]
    public async Task PGINV01_Reviewed_Invoice_Migration_Ddl_Executes_And_Rolls_Back()
    {
        var ddl = LoadReviewedDdlOnly();

        // Existing project guard requires localhost:5432 and the uniquely
        // named dedicated integration-test DB, never app/prod databases.
        var connectionString =
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var verify = new NpgsqlCommand(
            "SELECT current_database()", connection))
        {
            Assert.Equal(
                BuildingManagementPostgreSqlTestModule.DatabaseName,
                (string?)await verify.ExecuteScalarAsync());
        }

        // The test harness may have made these prerequisite tables with
        // EnsureCreated(), but its DB does not track EF migration history.
        // Always use this temporary schema, not EF database update.
        await using (var prereqs = new NpgsqlCommand(
            @"SELECT count(*) FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = 'public'
                AND c.relkind IN ('r','p')
                AND c.relname IN (
                  'BuildingManagementContracts',
                  'BuildingManagementRooms',
                  'BuildingManagementUtilityRates',
                  'BuildingManagementUtilityMeters')", connection))
        {
            Assert.Equal(4L, Convert.ToInt64(await prereqs.ExecuteScalarAsync()));
        }

        var schema = "bm_invoice_smoke_" + Guid.NewGuid().ToString("N");
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // PostgreSQL supports transactional DDL. Should anything fail,
            // disposing this transaction rolls back the entire test schema.
            await using (var createSchema = new NpgsqlCommand(
                $"CREATE SCHEMA \"{schema}\"", connection, transaction))
                await createSchema.ExecuteNonQueryAsync();

            await using (var setPath = new NpgsqlCommand(
                $"SET LOCAL search_path TO \"{schema}\", public",
                connection, transaction))
                await setPath.ExecuteNonQueryAsync();

            foreach (var statement in ddl)
            {
                await using var command = new NpgsqlCommand(
                    statement, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }

            await using (var tables = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM information_schema.tables
                  WHERE table_schema = @schema AND table_type = 'BASE TABLE'",
                connection, transaction))
            {
                tables.Parameters.AddWithValue("schema", schema);
                Assert.Equal(3L, Convert.ToInt64(await tables.ExecuteScalarAsync()));
            }

            await using (var indexes = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM pg_indexes
                  WHERE schemaname = @schema
                    AND indexname LIKE 'UX_BM_%'
                    AND indexdef ILIKE 'CREATE UNIQUE INDEX%'",
                connection, transaction))
            {
                indexes.Parameters.AddWithValue("schema", schema);
                Assert.Equal(5L, Convert.ToInt64(await indexes.ExecuteScalarAsync()));
            }

            await using (var foreignKeys = new NpgsqlCommand(
                @"SELECT COUNT(*) FROM pg_constraint fk
                  JOIN pg_namespace n ON n.oid = fk.connamespace
                  WHERE n.nspname = @schema
                    AND fk.contype = 'f'
                    AND fk.confdeltype = 'r'",
                connection, transaction))
            {
                foreignKeys.Parameters.AddWithValue("schema", schema);
                Assert.Equal(6L,
                    Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));
            }

            // Explicit rollback; no invoice tables, schema, or EF migration
            // history will be left in the test database.
            await transaction.RollbackAsync();
        }

        await using (var after = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_namespace WHERE nspname = @schema",
            connection))
        {
            after.Parameters.AddWithValue("schema", schema);
            Assert.Equal(0L, Convert.ToInt64(await after.ExecuteScalarAsync()));
        }
    }
}
