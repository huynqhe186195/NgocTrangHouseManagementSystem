using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// OPTIONAL / NEVER part of automatic CI database execution. The SQL is an
/// unapproved OFFLINE migration candidate embedded in this test assembly.
/// Only localhost:5432/ngoctrang_integration_test is permitted, and every
/// test creates an isolated, transaction-local schema, then ROLLS IT BACK.
/// No EF Migrate, DbMigrator, public search_path or production role grant.
/// </summary>
public sealed class BuildingOperatorGrantCandidatePostgreSql_Tests
{
    private const string Table = "BuildingManagementOperatorBuildingGrants";

    [Fact]
    public async Task PGMEMBER01_Grant_Schema_Rejects_Duplicate_And_Invalid_Data()
    {
        await InScratchAsync(async (connection, tx) =>
        {
            var building = Guid.NewGuid();
            var actor = Guid.NewGuid();
            await ExecuteAsync(connection, tx,
                $"""INSERT INTO "Buildings" ("Id") VALUES ('{building:D}')""");

            await ExecuteAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), actor, building, 1));

            // Same active user/building pair must be rejected by PostgreSQL.
            await ExpectFailureAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), actor, building, 1), "23505");

            // Invalid revision, unknown building and empty actor are
            // enforced even when a writer bypasses domain validation.
            await ExpectFailureAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), Guid.NewGuid(), building, 0), "23514");
            await ExpectFailureAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), "23503");
            await ExpectFailureAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), Guid.Empty, building, 1), "23514");

            // Explicit revoke preserves the historical row. The unique
            // slot then permits a NEW grant, without modifying old history.
            await ExecuteAsync(connection, tx,
                $"""
                UPDATE "{Table}"
                   SET "RevokedAtUtc" = '2026-10-28T00:00:00Z',
                       "RevokedByUserId" = '{Guid.NewGuid():D}',
                       "RevisionNumber" = 2
                 WHERE "OperatorUserId" = '{actor:D}';
                """);
            await ExecuteAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), actor, building, 1));

            Assert.Equal(2L, await ScalarLongAsync(connection, tx,
                $"""SELECT count(*) FROM "{Table}" """));
            Assert.Equal(1L, await ScalarLongAsync(connection, tx,
                $"""SELECT count(*) FROM "{Table}" WHERE "RevokedAtUtc" IS NULL"""));
        });
    }

    [Fact]
    public async Task PGMEMBER02_Down_Refuses_Loss_Of_Even_Revoked_History()
    {
        await InScratchAsync(async (connection, tx) =>
        {
            var building = Guid.NewGuid();
            var actor = Guid.NewGuid();
            await ExecuteAsync(connection, tx,
                $"""INSERT INTO "Buildings" ("Id") VALUES ('{building:D}')""");
            await ExecuteAsync(connection, tx, GrantInsert(
                Guid.NewGuid(), actor, building, 1));
            await ExecuteAsync(connection, tx,
                $"""
                UPDATE "{Table}"
                   SET "RevokedAtUtc" = '2026-10-28T00:00:00Z',
                       "RevokedByUserId" = '{Guid.NewGuid():D}',
                       "RevisionNumber" = 2;
                """);

            var down = LoadSql("SliceA_OperatorBuildingGrant_Down_Candidate.sql");
            await ExpectFailureAsync(connection, tx, down, "P0001");
            Assert.Equal(1L, await ScalarLongAsync(connection, tx,
                $"""SELECT count(*) FROM "{Table}" """));

            // Test cleanup may remove this row INSIDE a disposable schema
            // only. The real rollback script must never delete any history.
            await ExecuteAsync(connection, tx,
                $"""DELETE FROM "{Table}" """);
            await ExecuteAsync(connection, tx, down);
            Assert.Equal(1L, await ScalarLongAsync(connection, tx,
                $"""SELECT CASE WHEN to_regclass('"{Table}"') IS NULL THEN 1 ELSE 0 END"""));
            Assert.Equal(1L, await ScalarLongAsync(connection, tx,
                "SELECT count(*) FROM pg_tables " +
                "WHERE schemaname = current_schema() AND tablename = 'Buildings'"));
        });
    }

    [Fact]
    public async Task PGMEMBER03_Expiry_And_Revocation_Constraints_Protect_Stored_Audit()
    {
        await InScratchAsync(async (connection, tx) =>
        {
            var building = Guid.NewGuid();
            await ExecuteAsync(connection, tx,
                $"""INSERT INTO "Buildings" ("Id") VALUES ('{building:D}')""");
            await ExpectFailureAsync(connection, tx,
                GrantInsert(Guid.NewGuid(), Guid.NewGuid(), building, 1,
                    "'2026-10-26T00:00:00Z'"), "23514");
            await ExpectFailureAsync(connection, tx,
                GrantInsert(Guid.NewGuid(), Guid.NewGuid(), building, 1,
                    "NULL", "'2026-10-25T00:00:00Z'"), "23514");
            await ExpectFailureAsync(connection, tx,
                GrantInsert(Guid.NewGuid(), Guid.NewGuid(), building, 1,
                    "NULL", "'2026-10-28T00:00:00Z'"), "23514");
        });
    }

    private static string GrantInsert(
        Guid id, Guid user, Guid building, int revision,
        string expiry = "NULL", string revocation = "NULL")
        => $"""
           INSERT INTO "{Table}"
               ("Id", "OperatorUserId", "BuildingId", "GrantedByUserId",
                "GrantedAtUtc", "ExpiresAtUtc", "RevokedAtUtc",
                "RevokedByUserId", "RevisionNumber",
                "ExtraProperties", "ConcurrencyStamp")
           VALUES ('{id:D}', '{user:D}', '{building:D}', '{Guid.NewGuid():D}',
                   '2026-10-27T00:00:00Z', {expiry}, {revocation},
                   NULL, {revision}, '{{}}', '{Guid.NewGuid():N}');
           """;

    private static async Task InScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> check)
    {
        // Parse embedded, version-controlled SQL BEFORE touching any DB.
        var up = LoadSql("SliceA_OperatorBuildingGrant_Up_Candidate.sql");
        _ = LoadSql("SliceA_OperatorBuildingGrant_Down_Candidate.sql");

        var allowed = BuildingManagementPostgreSqlTestModule
            .RequireSafeConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false,
            CommandTimeout = 30
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            connection.Database);

        var schema = "bm_grant_candidate_" + Guid.NewGuid().ToString("N");
        await using (var tx = await connection.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(connection, tx,
                    $"""CREATE SCHEMA "{schema}" """);
                // Absolutely no 'public' fallback and bounded lock/SQL time.
                await ExecuteAsync(connection, tx,
                    $"""SET LOCAL search_path TO "{schema}" """);
                await ExecuteAsync(connection, tx,
                    "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(connection, tx,
                    "SET LOCAL statement_timeout = '20s'");
                Assert.Equal(schema, await ScalarStringAsync(connection, tx,
                    "SELECT current_schema()"));
                await ExecuteAsync(connection, tx,
                    """CREATE TABLE "Buildings" ("Id" uuid NOT NULL PRIMARY KEY);""");
                await ExecuteAsync(connection, tx, up);
                await check(connection, tx);
            }
            finally
            {
                await tx.RollbackAsync();
            }
        }

        Assert.Equal(0L, await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM pg_namespace " +
            $"WHERE nspname = '{schema}'"));
    }

    private static string LoadSql(string filename)
    {
        var stream = typeof(BuildingOperatorGrantCandidatePostgreSql_Tests)
            .Assembly.GetManifestResourceStream(filename)
            ?? throw new InvalidOperationException(
                "Missing OFFLINE grant SQL test resource: " + filename);
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            var sql = reader.ReadToEnd().TrimStart('\uFEFF');
            Assert.Contains($""" "{Table}" """.Trim(), sql,
                StringComparison.Ordinal);
            // Check executable statement starts, not ordinary review
            // comments such as "a grant is reusable only after revoke".
            Assert.False(Regex.IsMatch(sql,
                @"(?im)^\s*(GRANT|CREATE ROLE|ALTER ROLE|DROP DATABASE|BEGIN|COMMIT)\b"));
            Assert.DoesNotContain("public.", sql,
                StringComparison.OrdinalIgnoreCase);
            return sql;
        }
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx,
        string sql, string expectedState)
    {
        const string point = "grant_probe";
        await ExecuteAsync(connection, tx, $"SAVEPOINT {point}");
        string? actual = null;
        try
        {
            await ExecuteAsync(connection, tx, sql);
        }
        catch (PostgresException ex)
        {
            actual = ex.SqlState;
        }
        finally
        {
            await ExecuteAsync(connection, tx, $"ROLLBACK TO SAVEPOINT {point}");
            await ExecuteAsync(connection, tx, $"RELEASE SAVEPOINT {point}");
        }
        Assert.Equal(expectedState, actual);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return (string?)await command.ExecuteScalarAsync();
    }
}
