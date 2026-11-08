using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Restrict the TWO independent ACK/replay child-process database logins.
/// Creates ONLY non-superuser roles in the purpose-named disposable PG16
/// databases. Financial writer privileges are NOT granted to these roles.
/// This proves read-back and ACK can run without postgres superuser.
/// </summary>
internal static class D04BRestrictedAckDatabaseRolesGate
{
    internal static async Task InstallAsync()
    {
        if (Environment.GetEnvironmentVariable(
                "BM_PAYOS_D04_REAL_WRITER_TEST_DB") !=
                    "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
                != "disposable-only")
            throw new InvalidOperationException("D04 ACK roles require disposable DBs.");

        const string legacyAdmin =
            "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
        const string stagingAdmin =
            "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

        await GrantAsync(legacyAdmin, "payos_d04_legacy_test", new[]
        {
            """
            CREATE ROLE d04_ack_legacy_test LOGIN PASSWORD 'disposable-only'
              NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            """,
            "GRANT CONNECT ON DATABASE payos_d04_legacy_test TO d04_ack_legacy_test;",
            "GRANT USAGE ON SCHEMA public TO d04_ack_legacy_test;",
            """
            GRANT SELECT ON
              public."BuildingManagementPayOsD04ReceiptJournal",
              public."BuildingManagementPayOsD04Outbox",
              public."BuildingManagementPayments",
              public."BuildingManagementDepositSettlements",
              public."BuildingManagementPayOsD04BankReviews",
              public."BuildingManagementPayOsD04OwnerApprovals"
            TO d04_ack_legacy_test;
            """,
            """
            GRANT UPDATE ("AcknowledgedAtUtc","AttemptCount")
            ON public."BuildingManagementPayOsD04Outbox"
            TO d04_ack_legacy_test;
            """
        });
        await GrantAsync(stagingAdmin, "payos_ef_migration_test", new[]
        {
            """
            CREATE ROLE d04_ack_staging_test LOGIN PASSWORD 'disposable-only'
              NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
            """,
            "GRANT CONNECT ON DATABASE payos_ef_migration_test TO d04_ack_staging_test;",
            "GRANT USAGE ON SCHEMA public TO d04_ack_staging_test;",
            """
            GRANT SELECT ON
              public."BuildingManagementPayOsPaymentIntents",
              public."BuildingManagementPayOsWebhookInbox"
            TO d04_ack_staging_test;
            """,
            """
            GRANT UPDATE ("Status")
            ON public."BuildingManagementPayOsPaymentIntents"
            TO d04_ack_staging_test;
            """
        });

        // A compromised ACK-only process must NOT be able to modify
        // Payment/Settlement or insert its own journal/outbox.
        await VerifyDeniedAsync(
            "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
            "Username=d04_ack_legacy_test;Password=disposable-only;Pooling=false;Timeout=5",
            "BuildingManagementPayments",
            """
            UPDATE public."BuildingManagementPayments"
               SET "Status"=2 WHERE FALSE
            """);
        await VerifyDeniedAsync(
            "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
            "Username=d04_ack_staging_test;Password=disposable-only;Pooling=false;Timeout=5",
            "BuildingManagementPayOsWebhookInbox",
            """
            UPDATE public."BuildingManagementPayOsWebhookInbox"
               SET "Status"=2 WHERE FALSE
            """);
    }

    private static async Task GrantAsync(
        string adminConnection, string expectedDatabase, string[] sql)
    {
        await using var conn = new NpgsqlConnection(adminConnection);
        await conn.OpenAsync();
        await using (var probe = new NpgsqlCommand(
            "SELECT current_database()", conn))
            Assert.Equal(expectedDatabase,
                (string?)await probe.ExecuteScalarAsync());

        await using var tx = await conn.BeginTransactionAsync();
        foreach (var statement in sql)
        {
            await using var cmd = new NpgsqlCommand(statement, conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static async Task VerifyDeniedAsync(
        string roleConnection, string table, string update)
    {
        await using var conn = new NpgsqlConnection(roleConnection);
        await conn.OpenAsync();
        await using (var query = new NpgsqlCommand(
            """
            SELECT rolsuper FROM pg_roles WHERE rolname=current_user
            """, conn))
            Assert.False((bool)(await query.ExecuteScalarAsync() ?? true));

        await using (var privilege = new NpgsqlCommand(
            "SELECT has_table_privilege(current_user, @table, 'INSERT')", conn))
        {
            privilege.Parameters.AddWithValue("table", "public.\"" + table + "\"");
            Assert.False((bool)(await privilege.ExecuteScalarAsync() ?? true));
        }

        await using var denied = new NpgsqlCommand(update, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }
}
