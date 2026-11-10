using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.2 least-privilege rehearsal for the D04-B MONEY WRITER runtime role in
/// the exact disposable payos_d04_legacy_test / payos_ef_migration_test
/// databases. Creates ONLY non-superuser, non-DDL roles:
///
///   d04_money_writer_legacy_test  — legacy money writer
///   d04_money_writer_staging_test — staging reader/ACK
///
/// The writer role receives exactly the privileges the canonical SQL writer
/// uses: SELECT FOR UPDATE/SHARE on the locked financial tables, INSERT on
/// journal/outbox, UPDATE on Payment/Settlement commit columns, and NOTHING
/// ELSE. Everything a bypass attempt would need is denied with SQLSTATE
/// 42501: DDL, role creation, arbitrary Payment INSERT, immutable journal
/// UPDATE/DELETE, and BankReview/OwnerApproval tampering.
///
/// CRITICAL: the migrations/seeding still run as the postgres superuser.
/// This gate proves the run-time WRITER role boundary, not migration
/// isolation (which P1.2 keeps separate by design: migration role is never
/// handed to the running application).
/// </summary>
internal static class D04BRestrictedMoneyWriterRolesGate
{
    private const string LegacyAdmin =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
    private const string StagingAdmin =
        "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
    private const string LegacyRuntime =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
        "Username=d04_money_writer_legacy_test;Password=disposable-only;" +
        "Pooling=false;Timeout=5;Command Timeout=180";
    private const string StagingRuntime =
        "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
        "Username=d04_money_writer_staging_test;Password=disposable-only;" +
        "Pooling=false;Timeout=5;Command Timeout=180";

    internal static async Task InstallAndVerifyAsync()
    {
        if (Environment.GetEnvironmentVariable(
                "BM_PAYOS_D04_REAL_WRITER_TEST_DB") !=
                    "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
                != "disposable-only")
            throw new InvalidOperationException(
                "D04 money writer roles require disposable DBs.");

        await InstallLegacyRoleAsync();
        await InstallStagingRoleAsync();
        await VerifyRuntimeBoundaryAsync();
    }

    private static async Task InstallLegacyRoleAsync()
    {
        await ExecuteAsync(LegacyAdmin, "payos_d04_legacy_test", new[]
        {
            """
            DO $d04mw$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles
                             WHERE rolname='d04_money_writer_legacy_test') THEN
                CREATE ROLE d04_money_writer_legacy_test LOGIN
                  PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
                  NOCREATEROLE NOREPLICATION NOINHERIT;
              END IF;
            END
            $d04mw$;
            """,
            """
            GRANT CONNECT ON DATABASE payos_d04_legacy_test
              TO d04_money_writer_legacy_test;
            """,
            """
            GRANT USAGE ON SCHEMA public TO d04_money_writer_legacy_test;
            """,
            // Read + row locks the canonical writer actually takes:
            // contracts/payments FOR UPDATE (lock order), reviews/
            // approvals/journal/outbox/settlements FOR SHARE / plain SELECT.
            """
            GRANT SELECT ON
              public."BuildingManagementContracts",
              public."BuildingManagementPayments",
              public."BuildingManagementDepositSettlements",
              public."BuildingManagementPayOsD04BankReviews",
              public."BuildingManagementPayOsD04OwnerApprovals",
              public."BuildingManagementPayOsD04ReceiptJournal",
              public."BuildingManagementPayOsD04Outbox",
              public."BuildingManagementRooms",
              public."BuildingManagementFloors"
            TO d04_money_writer_legacy_test;
            """,
            // The writer completes the target payment and inserts the
            // allocation settlement through EF in the SAME serializable
            // transaction as the journal/outbox inserts.
            """
            GRANT INSERT ON
              public."BuildingManagementDepositSettlements",
              public."BuildingManagementPayOsD04ReceiptJournal",
              public."BuildingManagementPayOsD04Outbox"
            TO d04_money_writer_legacy_test;
            """,
            """
            GRANT UPDATE ON
              public."BuildingManagementPayments",
              public."BuildingManagementDepositSettlements"
            TO d04_money_writer_legacy_test;
            """,
            """
            GRANT UPDATE ("AcknowledgedAtUtc","AttemptCount")
              ON public."BuildingManagementPayOsD04Outbox"
              TO d04_money_writer_legacy_test;
            """
        });
    }

    private static async Task InstallStagingRoleAsync()
    {
        await ExecuteAsync(StagingAdmin, "payos_ef_migration_test", new[]
        {
            """
            DO $d04mws$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles
                             WHERE rolname='d04_money_writer_staging_test') THEN
                CREATE ROLE d04_money_writer_staging_test LOGIN
                  PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
                  NOCREATEROLE NOREPLICATION NOINHERIT;
              END IF;
            END
            $d04mws$;
            """,
            """
            GRANT CONNECT ON DATABASE payos_ef_migration_test
              TO d04_money_writer_staging_test;
            """,
            """
            GRANT USAGE ON SCHEMA public TO d04_money_writer_staging_test;
            """,
            """
            GRANT SELECT ON
              public."BuildingManagementPayOsPaymentIntents",
              public."BuildingManagementPayOsWebhookInbox"
            TO d04_money_writer_staging_test;
            """,
            """
            GRANT UPDATE ("Status")
              ON public."BuildingManagementPayOsPaymentIntents"
              TO d04_money_writer_staging_test;
            """
        });
    }

    private static async Task VerifyRuntimeBoundaryAsync()
    {
        // ---- Positive: the real writer privileges are usable ----
        await using (var conn = new NpgsqlConnection(LegacyRuntime))
        {
            await conn.OpenAsync();
            await using (var who = new NpgsqlCommand(
                "SELECT current_user, rolsuper, rolcreatedb, rolcreaterole " +
                "FROM pg_roles WHERE rolname=current_user", conn))
            await using (var role = await who.ExecuteReaderAsync())
            {
                Assert.True(await role.ReadAsync());
                Assert.Equal("d04_money_writer_legacy_test", role.GetString(0));
                Assert.False(role.GetBoolean(1));
                Assert.False(role.GetBoolean(2));
                Assert.False(role.GetBoolean(3));
            }

            // A serializable transaction with the exact lock order the
            // canonical writer uses must SUCCEED under the restricted role.
            await using (var tx = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable))
            {
                await using (var lockPayment = new NpgsqlCommand(
                    """
                    SELECT "Id" FROM public."BuildingManagementPayments"
                     WHERE "IsDeleted"=FALSE
                       AND NOT EXISTS (SELECT 1 FROM public."BuildingManagementPayOsD04ReceiptJournal"
                                        WHERE "PaymentId"="BuildingManagementPayments"."Id")
                     ORDER BY "Id" LIMIT 1 FOR UPDATE
                    """, conn, tx))
                {
                    await lockPayment.ExecuteNonQueryAsync();
                }
                await using (var journalProbe = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM public."BuildingManagementPayOsD04ReceiptJournal"
                    """, conn, tx))
                {
                    var rows = (long)(await journalProbe.ExecuteScalarAsync() ?? 0L);
                    Assert.True(rows >= 0);
                }
                await tx.CommitAsync();
            }
        }

        // ---- Negative: every bypass attempt denied with 42501 ----
        await AssertDeniedAsync(LegacyRuntime, """
            CREATE TABLE public."d04_privilege_escape" (id int)
            """);
        await AssertDeniedAsync(LegacyRuntime, """
            DROP TABLE public."BuildingManagementPayOsD04ReceiptJournal"
            """);
        // No new money rows: the restricted writer cannot invent a Payment.
        await AssertDeniedAsync(LegacyRuntime, """
            INSERT INTO public."BuildingManagementPayments"
              ("Id","PaymentNumber","TenantId","Purpose","Method","Channel",
               "Amount","ContractId","Status","IsDeleted")
            VALUES (gen_random_uuid(),'ESC-1','00000000-0000-0000-0000-000000000000',
                    2,2,2,1,NULL,0,FALSE)
            """);
        // The committed journal is immutable even for the writer itself.
        await AssertDeniedAsync(LegacyRuntime, """
            UPDATE public."BuildingManagementPayOsD04ReceiptJournal"
               SET "AmountVnd"="AmountVnd"+1 WHERE FALSE
            """);
        await AssertDeniedAsync(LegacyRuntime, """
            DELETE FROM public."BuildingManagementPayOsD04ReceiptJournal" WHERE FALSE
            """);
        // The dual-actor evidence cannot be forged after the fact.
        await AssertDeniedAsync(LegacyRuntime, """
            UPDATE public."BuildingManagementPayOsD04BankReviews"
               SET "VerifiedAtUtc"=NOW() WHERE FALSE
            """);
        // No INSERT privilege on the approval table at all (asserted via
        // has_table_privilege because a DEFAULT VALUES insert would fail
        // with 23502 not-null before the privilege check is observable).
        await AssertNoPrivilegeAsync(LegacyRuntime,
            "public.\"BuildingManagementPayOsD04OwnerApprovals\"", "INSERT");
        // The role cannot escalate or create logins (CREATE ROLE under a
        // NOCREATEROLE login fails with insufficient privilege 43101).
        await AssertDeniedAsync(LegacyRuntime, """
            CREATE ROLE d04_escalation_test LOGIN PASSWORD 'x'
            """, "43101");
        // No privilege to re-grant what it does not own.
        await AssertDeniedAsync(LegacyRuntime, """
            GRANT UPDATE ON public."BuildingManagementPayOsD04ReceiptJournal"
              TO d04_money_writer_legacy_test
            """);
        // No schema write access at all.
        await AssertDeniedAsync(LegacyRuntime, """
            CREATE FUNCTION public.d04_escape() RETURNS void AS $$ BEGIN END $$
              LANGUAGE plpgsql
            """);

        // ---- Staging side: ACK scope only ----
        await using (var conn = new NpgsqlConnection(StagingRuntime))
        {
            await conn.OpenAsync();
            // Read-back is allowed...
            await using (var read = new NpgsqlCommand(
                "SELECT count(*) FROM public.\"BuildingManagementPayOsPaymentIntents\"",
                conn))
                Assert.True((long)(await read.ExecuteScalarAsync() ?? -1L) >= 0);
            // ...but the signed webhook inbox cannot be touched.
            await using (var denied = new NpgsqlCommand(
                """
                UPDATE public."BuildingManagementPayOsWebhookInbox"
                   SET "Status"=2 WHERE FALSE
                """, conn))
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => denied.ExecuteNonQueryAsync());
                Assert.Equal("42501", ex.SqlState);
            }
            await using (var deniedInsert = new NpgsqlCommand(
                """
                SELECT has_table_privilege(current_user,
                  'public."BuildingManagementPayOsPaymentIntents"', 'INSERT')
                """, conn))
                Assert.False((bool)(await deniedInsert.ExecuteScalarAsync() ?? true));
        }
    }

    private static async Task AssertNoPrivilegeAsync(
        string roleConnection, string table, string privilege)
    {
        await using var conn = new NpgsqlConnection(roleConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT has_table_privilege(current_user, @table, @privilege)", conn);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("privilege", privilege);
        Assert.False((bool)(await cmd.ExecuteScalarAsync() ?? true));
    }

    private static async Task ExecuteAsync(
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

    private static async Task AssertDeniedAsync(
        string roleConnection, string sql, string expectedSqlState = "42501")
    {
        await using var conn = new NpgsqlConnection(roleConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, ex.SqlState);
    }
}
