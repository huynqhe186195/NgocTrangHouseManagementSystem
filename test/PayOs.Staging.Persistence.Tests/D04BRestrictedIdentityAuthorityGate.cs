using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Npgsql;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Dedicated Identity-only least-privilege SQL rehearsal in the exact
/// temporary payos_d04_identity_test PostgreSQL database. Tests the actual
/// row-leasing authority WITHOUT granting a database login direct access to
/// ABP Identity tables. Security-definer functions are created only after
/// native EF Identity tables have been seeded in the disposable 19a test.
///
/// IMPORTANT: DOES NOT prove Building financial writer or staging ACK roles
/// least-privileged. Their production rights require separate grants/gates.
/// </summary>
internal static class D04BRestrictedIdentityAuthorityGate
{
    private const string IdentityAdmin =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";
    private const string IdentityRuntime =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
        "Username=d04_identity_guard_test;Password=disposable-only;" +
        "Pooling=false;Timeout=5";

    internal static async Task VerifyAsync(
        BuildingManagementDbContext db, Guid buildingId,
        Guid reviewer, Guid owner)
    {
        if (Environment.GetEnvironmentVariable(
                "BM_PAYOS_D04_REAL_WRITER_TEST_DB") !=
                    "I_ACCEPT_ONLY_DISPOSABLE_PAYOS_D04_DB" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
                != "disposable-only")
            throw new InvalidOperationException(
                "Restricted Identity test refuses any non-disposable DB.");

        await using (var admin = new NpgsqlConnection(IdentityAdmin))
        {
            await admin.OpenAsync();
            await using (var name = new NpgsqlCommand(
                "SELECT current_database()", admin))
                Assert.Equal("payos_d04_identity_test",
                    (string?)await name.ExecuteScalarAsync());

            // All fixtures and permission functions are transactionally
            // published together. Revoke PUBLIC EXECUTE before COMMIT.
            await using var tx = await admin.BeginTransactionAsync();
            foreach (var sql in new[]
            {
                """
                DO $d04role$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles
                                 WHERE rolname='d04_identity_guard_test') THEN
                    CREATE ROLE d04_identity_guard_test LOGIN
                      PASSWORD 'disposable-only' NOSUPERUSER NOCREATEDB
                      NOCREATEROLE NOREPLICATION NOINHERIT;
                  END IF;
                END
                $d04role$;
                """,
                """
                GRANT CONNECT ON DATABASE payos_d04_identity_test
                  TO d04_identity_guard_test;
                """,
                """
                GRANT USAGE ON SCHEMA public TO d04_identity_guard_test;
                """,
                // Three functions use the actual native ABP table names
                // and lock exactly the same rows as the direct authority.
                """
                CREATE FUNCTION public.d04_test_lock_active_account(p_actor uuid)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $d04$
                DECLARE locked uuid;
                BEGIN
                  SELECT u."Id" INTO locked
                    FROM public."AbpUsers" u
                   WHERE u."Id"=p_actor AND u."IsDeleted"=FALSE
                     AND u."IsActive"=TRUE
                     AND (u."LockoutEnabled"=FALSE OR u."LockoutEnd" IS NULL
                          OR u."LockoutEnd"<=NOW())
                   FOR SHARE OF u;
                  RETURN locked IS NOT NULL;
                END
                $d04$;
                """,
                """
                CREATE FUNCTION public.d04_test_lock_direct_grant(
                    p_actor uuid, p_permission text)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $d04$
                DECLARE locked uuid;
                BEGIN
                  SELECT p."Id" INTO locked
                    FROM public."AbpPermissionGrants" p
                    JOIN public."AbpUsers" u ON u."Id"=p_actor
                   WHERE p."ProviderName"='U'
                     AND p."ProviderKey"=p_actor::text
                     AND p."Name"=p_permission
                     AND p."TenantId" IS NOT DISTINCT FROM u."TenantId"
                   FOR SHARE OF p;
                  RETURN locked IS NOT NULL;
                END
                $d04$;
                """,
                """
                CREATE FUNCTION public.d04_test_lock_owner_role(p_owner uuid)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $d04$
                DECLARE locked uuid;
                BEGIN
                  SELECT ur."UserId" INTO locked
                    FROM public."AbpUserRoles" ur
                    JOIN public."AbpUsers" u ON u."Id"=ur."UserId"
                    JOIN public."AbpRoles" r ON r."Id"=ur."RoleId"
                   WHERE ur."UserId"=p_owner AND r."Name"='Owner'
                     AND r."TenantId" IS NOT DISTINCT FROM u."TenantId"
                   ORDER BY r."Id" LIMIT 1
                   FOR SHARE OF ur,r;
                  RETURN locked IS NOT NULL;
                END
                $d04$;
                """,
                """
                REVOKE ALL ON FUNCTION
                  public.d04_test_lock_active_account(uuid) FROM PUBLIC;
                """,
                """
                REVOKE ALL ON FUNCTION
                  public.d04_test_lock_direct_grant(uuid,text) FROM PUBLIC;
                """,
                """
                REVOKE ALL ON FUNCTION
                  public.d04_test_lock_owner_role(uuid) FROM PUBLIC;
                """,
                """
                GRANT EXECUTE ON FUNCTION
                  public.d04_test_lock_active_account(uuid)
                  TO d04_identity_guard_test;
                """,
                """
                GRANT EXECUTE ON FUNCTION
                  public.d04_test_lock_direct_grant(uuid,text)
                  TO d04_identity_guard_test;
                """,
                """
                GRANT EXECUTE ON FUNCTION
                  public.d04_test_lock_owner_role(uuid)
                  TO d04_identity_guard_test;
                """
            })
            {
                await using var cmd = new NpgsqlCommand(sql, admin, tx);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        await using (var restricted = new NpgsqlConnection(IdentityRuntime))
        {
            await restricted.OpenAsync();
            await using var privileges = new NpgsqlCommand("""
                SELECT current_user,
                  has_table_privilege(current_user,'public."AbpUsers"','SELECT'),
                  has_table_privilege(current_user,'public."AbpUsers"','UPDATE'),
                  has_table_privilege(current_user,'public."AbpPermissionGrants"','SELECT'),
                  has_table_privilege(current_user,'public."AbpPermissionGrants"','UPDATE'),
                  has_table_privilege(current_user,'public."AbpRoles"','UPDATE'),
                  has_table_privilege(current_user,'public."AbpUserRoles"','UPDATE'),
                  has_schema_privilege(current_user,'public','CREATE')
                """, restricted);
            await using (var result = await privileges.ExecuteReaderAsync())
            {
                Assert.True(await result.ReadAsync());
                Assert.Equal("d04_identity_guard_test", result.GetString(0));
                for (var i = 1; i <= 7; i++)
                    Assert.False(result.GetBoolean(i));
            }

            // A direct table mutation is denied despite the function's
            // ability to lock its verified rows as a SECURITY DEFINER.
            await using var denied = new NpgsqlCommand("""
                UPDATE public."AbpUsers" SET "IsActive"=FALSE
                  WHERE "Id"=@owner
                """, restricted);
            denied.Parameters.AddWithValue("owner", owner);
            var failure = await Assert.ThrowsAsync<PostgresException>(
                () => denied.ExecuteNonQueryAsync());
            Assert.Equal("42501", failure.SqlState);
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] = "true",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY",
                ["ConnectionStrings:Default"] = IdentityRuntime,
                ["ConnectionStrings:BuildingManagement"] =
                    D04RealLegacyWriterAcceptance_Tests.Connection
            }).Build();
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns("Staging");
        var authority = new PayOsD04StagingDualAuthority(db, config, host.Object);

        await using var txBuilding = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable);
        await using (var lease = await authority.AcquireCommitLeaseAsync(
            buildingId, Guid.NewGuid(), reviewer, owner, CancellationToken.None))
        {
            // Holding a valid SQL-only lease in this transaction prevents
            // revocation; existing 19a tests check that revoke blocks.
            await txBuilding.CommitAsync();
        }
    }
}
