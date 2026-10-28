using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// EXPLICIT OPT-IN PostgreSQL concurrency probes. Independent transactions
/// cannot see uncommitted DDL: these tests temporarily COMMIT a uniquely
/// named schema in the dedicated localhost integration-test database and
/// DROP exactly that schema in finally. A process crash may leave a scratch
/// schema for manual cleanup. NEVER run on production or through CI Full.
/// </summary>
public sealed class BuildingOperatorGrantConcurrencyPostgreSql_Tests
{
    private const string Table = "BuildingManagementOperatorBuildingGrants";
    private const string TestProperties = "{\"source\":\"scratch\"}";
    private const string OptIn = "BM_PG_MEMBERSHIP_CONCURRENCY_OPT_IN";
    private const string Accepted = "I_ACCEPT_EPHEMERAL_SCHEMA";

    [Fact]
    public async Task PGMEMBER04_Concurrent_Active_Grant_Inserts_Exactly_One_Wins()
    {
        await InCommittedScratchAsync(async (cs, schema, building) =>
        {
            var actor = Guid.NewGuid();
            await using var winner = await OpenAsync(cs);
            await using var contender = await OpenAsync(cs);
            await using var winnerTx = await winner.BeginTransactionAsync();
            await using var contenderTx = await contender.BeginTransactionAsync();
            await EnterSchemaAsync(winner, winnerTx, schema);
            await EnterSchemaAsync(contender, contenderTx, schema);

            await ExecuteAsync(winner, winnerTx,
                GrantInsert(Guid.NewGuid(), actor, building));

            // Second SQL command is executed on a separate backend. Its
            // unique-index insert waits until the winner commits.
            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var competing = Task.Run(async () =>
            {
                started.TrySetResult(true);
                return await FailureStateAsync(contender, contenderTx,
                    GrantInsert(Guid.NewGuid(), actor, building));
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            Assert.False(competing.IsCompleted,
                "Second insert must be blocked by the uncommitted unique key.");

            await winnerTx.CommitAsync();
            Assert.Equal(PostgresErrorCodes.UniqueViolation,
                await competing.WaitAsync(TimeSpan.FromSeconds(15)));
            await contenderTx.RollbackAsync();

            await using var verify = await OpenAsync(cs);
            await using var verifyTx = await verify.BeginTransactionAsync();
            await EnterSchemaAsync(verify, verifyTx, schema);
            Assert.Equal(1L, await CountAsync(verify, verifyTx,
                $"SELECT count(*) FROM \"{Table}\" WHERE \"RevokedAtUtc\" IS NULL"));
            await verifyTx.RollbackAsync();
        });
    }

    [Fact]
    public async Task PGMEMBER05_Concurrent_Revokes_Require_Expected_Revision_Cas()
    {
        await InCommittedScratchAsync(async (cs, schema, building) =>
        {
            var actor = Guid.NewGuid();
            var grantId = Guid.NewGuid();
            await using (var seed = await OpenAsync(cs))
            {
                await using var seedTx = await seed.BeginTransactionAsync();
                await EnterSchemaAsync(seed, seedTx, schema);
                await ExecuteAsync(seed, seedTx,
                    GrantInsert(grantId, actor, building));
                await seedTx.CommitAsync();
            }

            await using var winner = await OpenAsync(cs);
            await using var stale = await OpenAsync(cs);
            await using var winnerTx = await winner.BeginTransactionAsync();
            await using var staleTx = await stale.BeginTransactionAsync();
            await EnterSchemaAsync(winner, winnerTx, schema);
            await EnterSchemaAsync(stale, staleTx, schema);

            Assert.Equal(1L, await CountAsync(winner, winnerTx,
                $"SELECT \"RevisionNumber\" FROM \"{Table}\" WHERE \"Id\" = '{grantId:D}'"));
            Assert.Equal(1L, await CountAsync(stale, staleTx,
                $"SELECT \"RevisionNumber\" FROM \"{Table}\" WHERE \"Id\" = '{grantId:D}'"));

            var first = RevokeIfRevision(grantId, 1);
            Assert.Equal(1, await ExecuteAsync(winner, winnerTx, first));

            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var second = Task.Run(async () =>
            {
                started.TrySetResult(true);
                return await ExecuteAsync(stale, staleTx, RevokeIfRevision(grantId, 1));
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            Assert.False(second.IsCompleted,
                "Stale revocation must wait for the competing row update.");

            await winnerTx.CommitAsync();
            Assert.Equal(0, await second.WaitAsync(TimeSpan.FromSeconds(15)));
            await staleTx.CommitAsync();

            await using var verify = await OpenAsync(cs);
            await using var verifyTx = await verify.BeginTransactionAsync();
            await EnterSchemaAsync(verify, verifyTx, schema);
            Assert.Equal(2L, await CountAsync(verify, verifyTx,
                $"SELECT \"RevisionNumber\" FROM \"{Table}\" WHERE \"Id\" = '{grantId:D}'"));
            Assert.Equal(1L, await CountAsync(verify, verifyTx,
                $"SELECT count(*) FROM \"{Table}\" WHERE \"Id\" = '{grantId:D}' AND \"RevokedAtUtc\" IS NOT NULL"));
            await verifyTx.RollbackAsync();
        });
    }

    [Fact]
    public async Task PGMEMBER06_Rollback_Waits_For_Concurrent_Grant_Then_Refuses_Drop()
    {
        await InCommittedScratchAsync(async (cs, schema, building) =>
        {
            await using var writer = await OpenAsync(cs);
            await using var rollback = await OpenAsync(cs);
            await using var writerTx = await writer.BeginTransactionAsync();
            await using var rollbackTx = await rollback.BeginTransactionAsync();
            await EnterSchemaAsync(writer, writerTx, schema);
            await EnterSchemaAsync(rollback, rollbackTx, schema);

            await ExecuteAsync(writer, writerTx,
                GrantInsert(Guid.NewGuid(), Guid.NewGuid(), building));

            var down = ReadCandidateSql(
                "SliceA_OperatorBuildingGrant_Down_Candidate.sql");
            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var rollbackAttempt = Task.Run(async () =>
            {
                started.TrySetResult(true);
                return await FailureStateAsync(rollback, rollbackTx, down);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            Assert.False(rollbackAttempt.IsCompleted,
                "ACCESS EXCLUSIVE rollback must wait for in-flight writes.");

            await writerTx.CommitAsync();
            Assert.Equal("P0001",
                await rollbackAttempt.WaitAsync(TimeSpan.FromSeconds(15)));
            await rollbackTx.RollbackAsync();

            await using var verify = await OpenAsync(cs);
            await using var verifyTx = await verify.BeginTransactionAsync();
            await EnterSchemaAsync(verify, verifyTx, schema);
            Assert.Equal(1L, await CountAsync(verify, verifyTx,
                $"SELECT count(*) FROM \"{Table}\""));
            await verifyTx.RollbackAsync();
        });
    }

    [Fact]
    public async Task PGMEMBER08_Independent_Login_Role_Cannot_Escalate_Grant_Or_Destroy_History()
    {
        // A separate LOGIN session, not SET ROLE on the table owner's
        // connection. The role is created only against the guarded CI/local
        // scratch PostgreSQL database and dropped in finally.
        if (Environment.GetEnvironmentVariable("BM_PG_RUNTIME_ROLE_OPT_IN") !=
            "I_ACCEPT_EPHEMERAL_NONOWNER_LOGIN")
            return;

        await InCommittedScratchAsync(async (cs, schema, building) =>
        {
            var role = "bm_slicea_runtime_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N") +
                           Guid.NewGuid().ToString("N");
            Assert.Matches(@"^bm_slicea_runtime_[0-9a-f]{32}$", role);
            var grantId = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            await using var admin = await OpenAsync(cs);
            var created = false;
            try
            {
                await ExecuteAsync(admin, null,
                    $"""CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '{password}'""");
                created = true;
                await ExecuteAsync(admin, null,
                    $"""GRANT USAGE ON SCHEMA "{schema}" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT SELECT ON TABLE "{schema}"."Buildings" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT SELECT, INSERT ON TABLE "{schema}"."{Table}" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT UPDATE ("RevokedAtUtc", "RevokedByUserId", "RevisionNumber", "ConcurrencyStamp") ON TABLE "{schema}"."{Table}" TO "{role}" """);

                var clientCs = new NpgsqlConnectionStringBuilder(cs.ConnectionString)
                {
                    Username = role,
                    Password = password,
                    SearchPath = schema,
                    Pooling = false,
                    CommandTimeout = 15
                };
                await using var runtime = new NpgsqlConnection(clientCs.ConnectionString);
                await runtime.OpenAsync();
                Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
                    runtime.Database);
                await using var tx = await runtime.BeginTransactionAsync();
                await ExecuteAsync(runtime, tx, "SET LOCAL lock_timeout = '3s'");
                Assert.Equal(schema, await ScalarStringAsync(
                    runtime, tx, "SELECT current_schema()"));
                Assert.Equal(role, await ScalarStringAsync(
                    runtime, tx, "SELECT current_user"));
                Assert.Equal(0L, await CountAsync(runtime, tx,
                    "SELECT count(*) FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolcreatedb OR rolcreaterole)"));
                Assert.Equal(0L, await CountAsync(runtime, tx,
                    "SELECT count(*) FROM pg_namespace WHERE nspname = current_schema() AND pg_get_userbyid(nspowner) = current_user"));

                await ExecuteAsync(runtime, tx,
                    GrantInsert(grantId, recipient, building));
                await ExecuteAsync(runtime, tx,
                    $"""
                    UPDATE "{Table}"
                       SET "RevokedAtUtc" = '2026-10-28T00:00:00Z',
                           "RevokedByUserId" = '{Guid.NewGuid():D}',
                           "RevisionNumber" = 2
                     WHERE "Id" = '{grantId:D}';
                    """);
                await ExpectPrivilegeDeniedAsync(runtime, tx,
                    $"""UPDATE "{Table}" SET "OperatorUserId" = '{Guid.NewGuid():D}' WHERE "Id" = '{grantId:D}'""");
                await ExpectPrivilegeDeniedAsync(runtime, tx,
                    $"""UPDATE "{Table}" SET "GrantedByUserId" = '{Guid.NewGuid():D}' WHERE "Id" = '{grantId:D}'""");
                await ExpectPrivilegeDeniedAsync(runtime, tx,
                    $"""DELETE FROM "{Table}" WHERE "Id" = '{grantId:D}'""");
                await ExpectPrivilegeDeniedAsync(runtime, tx,
                    $"""TRUNCATE TABLE "{Table}" """);
                await ExpectPrivilegeDeniedAsync(runtime, tx,
                    $"""ALTER TABLE "{Table}" ADD COLUMN "Unreviewed" boolean""");
                Assert.Equal(1L, await CountAsync(runtime, tx,
                    $"""SELECT count(*) FROM "{Table}" WHERE "Id" = '{grantId:D}' AND "RevokedAtUtc" IS NOT NULL"""));
                await tx.RollbackAsync();
            }
            finally
            {
                if (created)
                {
                    // Clear only privileges of this test-generated role,
                    // then drop it. Role creation is not production policy.
                    await ExecuteAsync(admin, null,
                        $"""REVOKE UPDATE ("RevokedAtUtc", "RevokedByUserId", "RevisionNumber", "ConcurrencyStamp") ON TABLE "{schema}"."{Table}" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON TABLE "{schema}"."{Table}" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON TABLE "{schema}"."Buildings" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON SCHEMA "{schema}" FROM "{role}" """);
                    await ExecuteAsync(admin, null, $"""DROP ROLE "{role}" """);
                }
            }
        });
    }

    [Fact]
    public async Task PGMEMBER09_ReadOnly_Role_Audit_Passes_LeastPrivilege_And_Rejects_Escalation()
    {
        if (Environment.GetEnvironmentVariable("BM_PG_RUNTIME_ROLE_OPT_IN") !=
            "I_ACCEPT_EPHEMERAL_NONOWNER_LOGIN")
            return;

        // Exercise the EXACT additive EF migration SQL in a disposable
        // committed schema. All global test roles are revoked/dropped in
        // finally; there is NO user-selected or application database.
        await InCommittedScratchAsync(async (adminBuilder, schema, _) =>
        {
            var role = "bm_role_audit_" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N") +
                           Guid.NewGuid().ToString("N");
            Assert.Matches(@"^bm_role_audit_[0-9a-f]{32}$", role);
            var adminSettings = new NpgsqlConnectionStringBuilder(
                adminBuilder.ConnectionString) { SearchPath = schema };
            await using var admin = await OpenAsync(adminSettings);
            var created = false;
            try
            {
                // Migration is not approved/deployed: source SQL runs
                // exclusively in the randomized scratch schema.
                await using (var tx = await admin.BeginTransactionAsync())
                {
                    var migration = new BuildingManagement.Migrations
                        .SliceAOwnerRecoveryApprovals();
                    foreach (var operation in migration.UpOperations.OfType<
                        Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
                        await ExecuteAsync(admin, tx, operation.Sql);
                    await tx.CommitAsync();
                }

                await ExecuteAsync(admin, null,
                    $"""CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '{password}'""");
                created = true;
                await ExecuteAsync(admin, null,
                    $"""GRANT USAGE ON SCHEMA "{schema}" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT SELECT, INSERT ON TABLE "{schema}"."{Table}" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT UPDATE ("RevokedAtUtc", "RevokedByUserId", "RevisionNumber", "ConcurrencyStamp") ON TABLE "{schema}"."{Table}" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT SELECT, INSERT ON TABLE "{schema}"."BuildingManagementOwnerRecoverySignoffs" TO "{role}" """);
                await ExecuteAsync(admin, null,
                    $"""GRANT UPDATE ("ConsumedAtUtc", "RecoveryGrantId") ON TABLE "{schema}"."BuildingManagementOwnerRecoverySignoffs" TO "{role}" """);

                using var stream = typeof(BuildingOperatorGrantConcurrencyPostgreSql_Tests)
                    .Assembly.GetManifestResourceStream(
                        "SliceA_RuntimeDbRole_ReadOnlyAudit.sql")
                    ?? throw new InvalidOperationException(
                        "Missing version-controlled read-only audit SQL.");
                using var reader = new StreamReader(stream);
                var audit = reader.ReadToEnd().TrimStart('\uFEFF');
                // Only an explicit, single SELECT is allowed. No psql
                // variables, scripts or mutable SQL in this audit artifact.
                Assert.Equal(1, Regex.Matches(audit,
                    @"(?im)^SELECT\s+runtime_login").Count);
                Assert.False(Regex.IsMatch(audit,
                    @"(?im)^\s*(CREATE|ALTER|DROP|GRANT|REVOKE|UPDATE|DELETE|INSERT|TRUNCATE|SET|DO|CALL)\b"));

                var runtimeOptions = new NpgsqlConnectionStringBuilder(
                    adminSettings.ConnectionString)
                {
                    Username = role, Password = password,
                    Pooling = false, CommandTimeout = 15
                };
                await using var runtime = await OpenAsync(runtimeOptions);

                async Task<(bool Role, bool Grant, bool Recovery, bool All)> AuditAsync(
                    NpgsqlConnection connection)
                {
                    await using var command = new NpgsqlCommand(audit, connection);
                    await using var results = await command.ExecuteReaderAsync();
                    Assert.True(await results.ReadAsync());
                    Assert.Equal(role, results.GetString(0));
                    Assert.Equal(role, results.GetString(1));
                    Assert.Equal(schema, results.GetString(2));
                    Assert.True(results.GetBoolean(3));
                    Assert.True(results.GetBoolean(4));
                    return (results.GetBoolean(5), results.GetBoolean(6),
                        results.GetBoolean(7), results.GetBoolean(8));
                }

                var clean = await AuditAsync(runtime);
                Assert.True(clean.Role);
                Assert.True(clean.Grant);
                Assert.True(clean.Recovery);
                Assert.True(clean.All);

                // Prove the audit detects effective overprivilege without
                // performing an unsafe UPDATE through that role.
                await ExecuteAsync(admin, null,
                    $"""GRANT UPDATE ("OperatorUserId") ON TABLE "{schema}"."{Table}" TO "{role}" """);
                var unsafeResult = await AuditAsync(runtime);
                Assert.True(unsafeResult.Role);
                Assert.False(unsafeResult.Grant);
                Assert.True(unsafeResult.Recovery);
                Assert.False(unsafeResult.All);
            }
            finally
            {
                if (created)
                {
                    await ExecuteAsync(admin, null,
                        $"""REVOKE UPDATE ("OperatorUserId", "RevokedAtUtc", "RevokedByUserId", "RevisionNumber", "ConcurrencyStamp") ON TABLE "{schema}"."{Table}" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON TABLE "{schema}"."{Table}" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE UPDATE ("ConsumedAtUtc", "RecoveryGrantId") ON TABLE "{schema}"."BuildingManagementOwnerRecoverySignoffs" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON TABLE "{schema}"."BuildingManagementOwnerRecoverySignoffs" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""REVOKE ALL ON SCHEMA "{schema}" FROM "{role}" """);
                    await ExecuteAsync(admin, null,
                        $"""DROP ROLE "{role}" """);
                }
            }
        });
    }

    private static async Task ExpectPrivilegeDeniedAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        await ExecuteAsync(connection, tx, "SAVEPOINT runtime_privilege_probe");
        string? state = null;
        try
        {
            await ExecuteAsync(connection, tx, sql);
        }
        catch (PostgresException error)
        {
            state = error.SqlState;
        }
        finally
        {
            await ExecuteAsync(connection, tx,
                "ROLLBACK TO SAVEPOINT runtime_privilege_probe");
            await ExecuteAsync(connection, tx,
                "RELEASE SAVEPOINT runtime_privilege_probe");
        }
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state);
    }

    private static string GrantInsert(Guid id, Guid user, Guid building)
        => $"""
           INSERT INTO "{Table}"
               ("Id", "OperatorUserId", "BuildingId", "GrantedByUserId",
                "GrantedAtUtc", "ExpiresAtUtc", "RevokedAtUtc",
                "RevokedByUserId", "RevisionNumber",
                "ExtraProperties", "ConcurrencyStamp")
           VALUES ('{id:D}', '{user:D}', '{building:D}', '{Guid.NewGuid():D}',
                   '2026-10-27T00:00:00Z', NULL, NULL,
                   NULL, 1, '{TestProperties}', '{Guid.NewGuid():N}');
           """;

    private static string RevokeIfRevision(Guid id, int expected)
        => $"""
           UPDATE "{Table}"
              SET "RevokedAtUtc" = '2026-10-28T00:00:00Z',
                  "RevokedByUserId" = '{Guid.NewGuid():D}',
                  "RevisionNumber" = "RevisionNumber" + 1
            WHERE "Id" = '{id:D}' AND "RevisionNumber" = {expected}
              AND "RevokedAtUtc" IS NULL;
           """;

    private static async Task InCommittedScratchAsync(
        Func<NpgsqlConnectionStringBuilder, string, Guid, Task> exercise)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptIn),
                Accepted, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Safety stop: explicit opt-in is required for a committed " +
                "ephemeral PostgreSQL schema. Use the guarded local script.");

        // Verify the exact resource before any database connection.
        var up = ReadCandidateSql(
            "SliceA_OperatorBuildingGrant_Up_Candidate.sql");
        _ = ReadCandidateSql(
            "SliceA_OperatorBuildingGrant_Down_Candidate.sql");

        var builder = new NpgsqlConnectionStringBuilder(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString())
        {
            Pooling = false,
            CommandTimeout = 20
        };
        var schema = "bm_grant_race_" + Guid.NewGuid().ToString("N");
        var building = Guid.NewGuid();
        var committed = false;

        await using var setup = await OpenAsync(builder);
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            setup.Database);
        try
        {
            await using (var tx = await setup.BeginTransactionAsync())
            {
                await ExecuteAsync(setup, tx, $"CREATE SCHEMA \"{schema}\"");
                await EnterSchemaAsync(setup, tx, schema);
                await ExecuteAsync(setup, tx,
                    """CREATE TABLE "Buildings" ("Id" uuid NOT NULL PRIMARY KEY);""");
                await ExecuteAsync(setup, tx, up);
                await ExecuteAsync(setup, tx,
                    $"INSERT INTO \"Buildings\" (\"Id\") VALUES ('{building:D}')");
                await tx.CommitAsync();
                committed = true;
            }

            await exercise(builder, schema, building);
        }
        finally
        {
            if (committed)
            {
                if (!Regex.IsMatch(schema, @"\Abm_grant_race_[0-9a-f]{32}\z",
                        RegexOptions.CultureInvariant))
                    throw new InvalidOperationException(
                        "Safety stop: scratch-schema identity invalid.");

                // The ONLY committed destructive statement in this test:
                // remove precisely the randomized schema created above.
                // Never use public or a caller-provided schema name.
                await using var cleaner = await OpenAsync(builder);
                Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
                    cleaner.Database);
                await ExecuteAsync(cleaner, null,
                    $"DROP SCHEMA \"{schema}\" CASCADE");
                Assert.Equal(0L, await CountAsync(cleaner, null,
                    "SELECT count(*) FROM pg_namespace " +
                    $"WHERE nspname = '{schema}'"));
            }
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(
        NpgsqlConnectionStringBuilder builder)
    {
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        if (!string.Equals(connection.Database,
                BuildingManagementPostgreSqlTestModule.DatabaseName,
                StringComparison.Ordinal))
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException("Unexpected database after connect.");
        }
        return connection;
    }

    private static async Task EnterSchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string schema)
    {
        await ExecuteAsync(connection, tx,
            $"SET LOCAL search_path TO \"{schema}\"");
        await ExecuteAsync(connection, tx, "SET LOCAL lock_timeout = '3s'");
        await ExecuteAsync(connection, tx, "SET LOCAL statement_timeout = '15s'");
        var current = await ScalarStringAsync(connection, tx, "SELECT current_schema()");
        Assert.Equal(schema, current);
    }

    private static string ReadCandidateSql(string filename)
    {
        using var stream = typeof(BuildingOperatorGrantConcurrencyPostgreSql_Tests)
            .Assembly.GetManifestResourceStream(filename)
            ?? throw new InvalidOperationException(
                "Missing version-controlled grant SQL resource: " + filename);
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd().TrimStart('\uFEFF');
        Assert.Contains(Table, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("public.", sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(sql,
            @"(?im)^\s*(?:GRANT\b|CREATE\s+ROLE\b|ALTER\s+ROLE\b|DROP\s+DATABASE\b|START\s+TRANSACTION\b|COMMIT\b|BEGIN\s*(?:TRANSACTION\b|;))"));
        return sql;
    }

    private static async Task<string?> FailureStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        try
        {
            await ExecuteAsync(connection, tx, sql);
            return null;
        }
        catch (PostgresException error)
        {
            return error.SqlState;
        }
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
