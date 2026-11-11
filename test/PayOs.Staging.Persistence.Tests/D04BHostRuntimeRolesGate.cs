using System;
using System.Data;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// P1.3 restricted HOST runtime roles for the D04-B lane. Distinct from the
/// P1.2 d04_money_writer_* rehearsal roles BY DESIGN: the Host runtime must
/// also record independent bank reviews and Owner approvals (the dual-actor
/// evidence chain), so its legacy role carries INSERT on BankReviews and
/// OwnerApprovals — grants the P1.2 rehearsal gate deliberately denies to
/// prove the money writer alone cannot forge dual-actor evidence.
///
/// This gate proves the HOST connection itself can be switched away from
/// the disposable superuser: the exact SQL the Host issues on
/// payos_d04_legacy_test / payos_ef_migration_test (review SELECT FOR
/// SHARE, review/approval INSERT, writer journal/outbox INSERT,
/// EF Payment complete + settlement insert, intent MarkPosted UPDATE,
/// outbox ACK UPDATE) commits under the restricted role, while DDL,
/// role escalation, immutable-row tampering, superuser and cross-slot
/// database access are all denied.
///
/// CRITICAL: migrations/seeding still run as postgres on a separate
/// connection; these roles are never handed the migration path.
/// </summary>
internal static class D04BHostRuntimeRolesGate
{
    private const string LegacyAdmin =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
    private const string StagingAdmin =
        "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
    private const string LegacyHost =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_legacy_test;" +
        "Username=d04_d04b_host_legacy_test;Password=disposable-only;" +
        "Pooling=false;Timeout=5;Command Timeout=180";
    private const string StagingHost =
        "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
        "Username=d04_d04b_host_staging_test;Password=disposable-only;" +
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
                "D04 host runtime roles require disposable DBs.");

        await InstallLegacyRoleAsync();
        await InstallStagingRoleAsync();
        await VerifyRuntimeBoundaryAsync();
    }

    private static async Task InstallLegacyRoleAsync()
    {
        await ExecuteAsync(LegacyAdmin, "payos_d04_legacy_test", new[]
        {
            """
            DO $d04hr$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles
                             WHERE rolname='d04_d04b_host_legacy_test') THEN
                CREATE ROLE d04_d04b_host_legacy_test LOGIN
                  PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
                  NOCREATEROLE NOREPLICATION NOINHERIT;
              END IF;
            END
            $d04hr$;
            """,
            """
            GRANT CONNECT ON DATABASE payos_d04_legacy_test
              TO d04_d04b_host_legacy_test;
            """,
            """
            GRANT USAGE ON SCHEMA public TO d04_d04b_host_legacy_test;
            """,
            // Reads + row locks the Host D04-B lane actually takes:
            // review/approval SELECT FOR SHARE, ResolveBuildingAsync
            // payments/contracts/rooms/floors, DualAuthority
            // OperatorBuildingGrants/Buildings FOR SHARE, deposit-coverage
            // check on settlements, writer lock order, receipt store joins.
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
              public."BuildingManagementFloors",
              public."Buildings",
              public."BuildingManagementOperatorBuildingGrants",
              public."BuildingManagementContractTenants",
              public."BuildingManagementBankEvidenceClaims"
            TO d04_d04b_host_legacy_test;
            """,
            // Review + Owner-approval evidence INSERTs (the dual-actor
            // chain the P1.2 rehearsal role deliberately lacks).
            """
            GRANT INSERT ON
              public."BuildingManagementPayOsD04BankReviews",
              public."BuildingManagementPayOsD04OwnerApprovals"
            TO d04_d04b_host_legacy_test;
            """,
            // Writer money rows: settlement/journal/outbox INSERT and
            // EF Payment.Complete + settlement UPDATE.
            """
            GRANT INSERT ON
              public."BuildingManagementDepositSettlements",
              public."BuildingManagementPayOsD04ReceiptJournal",
              public."BuildingManagementPayOsD04Outbox"
            TO d04_d04b_host_legacy_test;
            """,
            """
            GRANT UPDATE ON
              public."BuildingManagementPayments",
              public."BuildingManagementDepositSettlements"
            TO d04_d04b_host_legacy_test;
            """,
            // SELECT FOR SHARE / FOR UPDATE requires the UPDATE privilege
            // in PostgreSQL (probed on PG16: SELECT alone gets 42501 on
            // FOR SHARE). The review path locks BankReviews/OwnerApprovals
            // and the DualAuthority lock path takes FOR SHARE OF g,b on
            // OperatorBuildingGrants/Buildings, and the journal INSERT
            // trigger BM_PayOsD04ValidateReceipt FOR SHAREs
            // Contracts/Rooms/Floors, so all seven carry UPDATE too.
            // Actual mutation of the immutable evidence rows stays
            // blocked by the 23514 reject-triggers asserted below
            // — privilege is not mutation.
            """
            GRANT UPDATE ON
              public."BuildingManagementPayOsD04BankReviews",
              public."BuildingManagementPayOsD04OwnerApprovals",
              public."BuildingManagementOperatorBuildingGrants",
              public."Buildings",
              public."BuildingManagementContracts",
              public."BuildingManagementRooms",
              public."BuildingManagementFloors"
            TO d04_d04b_host_legacy_test;
            """,
            // Outbox ACK metadata is the ONLY mutable part of the audit.
            """
            GRANT UPDATE ("AcknowledgedAtUtc","AttemptCount")
              ON public."BuildingManagementPayOsD04Outbox"
              TO d04_d04b_host_legacy_test;
            """
        });
    }

    private static async Task InstallStagingRoleAsync()
    {
        await ExecuteAsync(StagingAdmin, "payos_ef_migration_test", new[]
        {
            """
            DO $d04hs$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles
                             WHERE rolname='d04_d04b_host_staging_test') THEN
                CREATE ROLE d04_d04b_host_staging_test LOGIN
                  PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
                  NOCREATEROLE NOREPLICATION NOINHERIT;
              END IF;
            END
            $d04hs$;
            """,
            """
            GRANT CONNECT ON DATABASE payos_ef_migration_test
              TO d04_d04b_host_staging_test;
            """,
            """
            GRANT USAGE ON SCHEMA public TO d04_d04b_host_staging_test;
            """,
            // ACK: lock + read intent, read webhook inbox, MarkPosted.
            // Column list matches what EF actually writes for MarkPosted
            // under ConfigureByConvention: Status + ConcurrencyStamp +
            // ExtraProperties (changed-columns-only UPDATE batches).
            """
            GRANT SELECT ON
              public."BuildingManagementPayOsPaymentIntents",
              public."BuildingManagementPayOsWebhookInbox"
            TO d04_d04b_host_staging_test;
            """,
            """
            GRANT UPDATE ("Status","ConcurrencyStamp","ExtraProperties")
              ON public."BuildingManagementPayOsPaymentIntents"
              TO d04_d04b_host_staging_test;
            """
        });
    }

    private static async Task VerifyRuntimeBoundaryAsync()
    {
        // ---- Positive: the exact Host D04-B SQL works under the role ----
        await using (var conn = new NpgsqlConnection(LegacyHost))
        {
            await conn.OpenAsync();
            await using (var who = new NpgsqlCommand(
                "SELECT current_user, rolsuper FROM pg_roles WHERE rolname=current_user",
                conn))
            await using (var role = await who.ExecuteReaderAsync())
            {
                Assert.True(await role.ReadAsync());
                Assert.Equal("d04_d04b_host_legacy_test", role.GetString(0));
                Assert.False(role.GetBoolean(1));
            }

            // Review-path SELECT FOR SHARE (ManualEvidenceService) and the
            // DualAuthority building-grant FOR SHARE both commit.
            await using (var tx = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable))
            {
                await ExecAsync(conn, tx, """
                    SELECT "Id" FROM public."BuildingManagementPayOsD04BankReviews"
                     WHERE FALSE FOR SHARE
                    """);
                await ExecAsync(conn, tx, """
                    SELECT g."Id" FROM public."BuildingManagementOperatorBuildingGrants" g
                     JOIN public."Buildings" b ON b."Id"=g."BuildingId"
                     WHERE FALSE FOR SHARE OF g,b
                    """);
                await ExecAsync(conn, tx, """
                    SELECT "Id" FROM public."BuildingManagementPayments"
                     WHERE FALSE FOR UPDATE
                    """);
                // The journal INSERT trigger BM_PayOsD04ValidateReceipt
                // FOR SHAREs the scope chain Contracts -> Rooms -> Floors
                // (plus the tables above) — prove those lock under the
                // role as well.
                await ExecAsync(conn, tx, """
                    SELECT "Id" FROM public."BuildingManagementContracts"
                     WHERE FALSE FOR SHARE
                """);
                await ExecAsync(conn, tx, """
                    SELECT f."BuildingId"
                      FROM public."BuildingManagementRooms" r
                      JOIN public."BuildingManagementFloors" f
                        ON f."Id"=r."FloorId"
                     WHERE FALSE FOR SHARE OF r,f
                """);
                await tx.CommitAsync();
            }
        }

        // ---- Negative: every bypass attempt denied with 42501 ----
        // DDL / schema escapes.
        await AssertDeniedAsync(LegacyHost, """
            CREATE TABLE public."d04_host_escape" (id int)
            """);
        await AssertDeniedAsync(LegacyHost, """
            CREATE ROLE d04_host_escalation_test LOGIN PASSWORD 'x'
            """);
        // Committed audit rows stay immutable even for the Host role.
        // UPDATE on BankReviews is now privilege-GRANTED (FOR SHARE locks
        // need it) — the immutable-row reject-trigger (23514) is what
        // stops the mutation. Seed a real row via the role INSERT
        // grant and prove the immutable-row reject-trigger (23514),
        // not the privilege system, blocks the mutation.
        // Pick an already-persisted review row (the acceptance test
        // commits real evidence rows before the roles gate runs).
        var immutableReviewId = await SelectExistingReviewIdAsync();
        await AssertImmutableUpdateRejectedAsync(LegacyHost,
            """
            UPDATE public."BuildingManagementPayOsD04BankReviews"
               SET "VerifiedAtUtc"=NOW() WHERE "Id"=@id
            """, immutableReviewId);
        // DELETE is never granted at all (the role only needs
        // SELECT/INSERT/UPDATE-for-locks) — stays privilege-denied.
        await AssertDeniedAsync(LegacyHost, """
            UPDATE public."BuildingManagementPayOsD04ReceiptJournal"
               SET "AmountVnd"="AmountVnd"+1 WHERE FALSE
            """);
        await AssertDeniedAsync(LegacyHost, """
            DELETE FROM public."BuildingManagementPayOsD04OwnerApprovals" WHERE FALSE
            """);
        // No invented Payment and no outbox non-ACK tampering. The two ACK
        // metadata columns are the ONLY outbox columns the role may update.
        await AssertDeniedAsync(LegacyHost, """
            INSERT INTO public."BuildingManagementPayments"
              ("Id","PaymentNumber","TenantId","Purpose","Method","Channel",
               "Amount","ContractId","Status","IsDeleted")
            VALUES (gen_random_uuid(),'ESC-H','00000000-0000-0000-0000-000000000000',
                    2,2,2,1,NULL,0,FALSE)
            """);
        await AssertDeniedAsync(LegacyHost, """
            UPDATE public."BuildingManagementPayOsD04Outbox"
               SET "ReceiptId"="ReceiptId" WHERE FALSE
            """);
        await AssertDeniedAsync(LegacyHost, """
            UPDATE public."BuildingManagementPayOsD04Outbox"
               SET "SourceKey"="SourceKey" WHERE FALSE
            """);
        // Cross-slot: CONNECT is granted to PUBLIC by default on every
        // database, but no table privilege exists outside the own slot —
        // reaching across must fail on the first table access.
        await AssertDeniedTableAccessAsync(LegacyHost, "payos_d04_identity_test",
            "SELECT 1 FROM \"AbpUsers\"");
        await AssertDeniedTableAccessAsync(LegacyHost, "payos_ef_migration_test",
            "SELECT 1 FROM \"BuildingManagementPayOsPaymentIntents\"");

        // ---- Staging side ----
        await using (var conn = new NpgsqlConnection(StagingHost))
        {
            await conn.OpenAsync();
            // MarkPosted-shaped UPDATE on a zero-row predicate is
            // privilege-checked and succeeds trivially; the inbox must
            // stay read-only.
            await ExecAsync(conn, null, """
                UPDATE public."BuildingManagementPayOsPaymentIntents"
                   SET "Status"=3 WHERE FALSE
                """);
        }
        await AssertDeniedAsync(StagingHost, """
            UPDATE public."BuildingManagementPayOsWebhookInbox"
               SET "Status"=2 WHERE FALSE
            """);
        await AssertNoPrivilegeAsync(StagingHost,
            "public.\"BuildingManagementPayOsPaymentIntents\"", "INSERT");
        await AssertNoPrivilegeAsync(StagingHost,
            "public.\"BuildingManagementPayOsWebhookInbox\"", "INSERT");
        await AssertDeniedTableAccessAsync(StagingHost, "payos_d04_legacy_test",
            "SELECT 1 FROM \"BuildingManagementPayments\"");
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

    private static async Task ExecAsync(NpgsqlConnection conn,
        NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = tx is null
            ? new NpgsqlCommand(sql, conn)
            : new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The immutable-row trigger assertion needs a real committed review
    /// row: the acceptance test seeds genuine evidence rows before this
    /// gate runs. (A WHERE FALSE statement matches zero rows, so no
    /// row-level trigger would fire at all.)
    /// </summary>
    private static async Task<Guid> SelectExistingReviewIdAsync()
    {
        await using var conn = new NpgsqlConnection(LegacyAdmin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "Id" FROM public."BuildingManagementPayOsD04BankReviews"
             ORDER BY "VerifiedAtUtc" LIMIT 1
            """, conn);
        return await cmd.ExecuteScalarAsync() is Guid id
            ? id
            : throw new InvalidOperationException(
                "No committed bank review row exists for the immutability check.");
    }

    /// <summary>
    /// The role HOLDS the UPDATE privilege (FOR SHARE locks require it) but
    /// the immutable-row reject-trigger still fires: assert PostgreSQL
    /// check-violation 23514 rather than privilege-denied 42501.
    /// </summary>
    private static async Task AssertImmutableUpdateRejectedAsync(
        string roleConnection, string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(roleConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    private static async Task AssertDeniedAsync(string roleConnection, string sql)
    {
        await using var conn = new NpgsqlConnection(roleConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    private static async Task AssertDeniedTableAccessAsync(
        string roleConnection, string database, string sql)
    {
        var cs = new NpgsqlConnectionStringBuilder(roleConnection)
        {
            Database = database
        };
        // Table access across slots must fail even though CONNECT is
        // granted to PUBLIC: the role holds no table privilege there.
        await using var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteScalarAsync());
        Assert.Equal("42501", ex.SqlState);
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
}
