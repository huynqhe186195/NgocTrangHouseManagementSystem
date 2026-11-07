using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BuildingManagement.EntityFrameworkCore.PayOsMigrations;

/// <summary>
/// Only the migration review tool should construct this context.
/// Offline scaffolding points at loopback port 1 (no listener).
/// Disposable PG acceptance is only ever loopback and a fixed test database.
/// This factory deliberately never resolves application or production secrets.
/// </summary>
public sealed class PayOsPaymentMigrationDbContextFactory :
    IDesignTimeDbContextFactory<PayOsPaymentMigrationDbContext>
{
    public PayOsPaymentMigrationDbContext CreateDbContext(string[] args)
    {
        var disposable = Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") == "1";
        var password = disposable
            ? Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD")
            : "offline-only";
        if (disposable && string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Disposable test password missing.");

        // CI uses localhost:5432; the one-command Windows local rehearsal
        // uses a NEW disposable postgres:16 container bound to 127.0.0.1:15432.
        // Never allow arbitrary hosts, ports or database names through env.
        var testPort = Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") ?? "5432";
        if (disposable && testPort != "5432" && testPort != "15432")
            throw new InvalidOperationException(
                "Disposable payOS tests permit only loopback port 5432 (CI) or 15432 (local).");

        var connection = disposable
            ? "Host=127.0.0.1;Port=" + testPort + ";Database=payos_ef_migration_test;Username=postgres;Password=" + password + ";Timeout=3"
            : "Host=127.0.0.1;Port=1;Database=payos_offline_scaffold;Username=offline;Password=offline-only;Timeout=1";

        var options = new DbContextOptionsBuilder<PayOsPaymentMigrationDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_PayOs"))
            .Options;
        return new PayOsPaymentMigrationDbContext(options);
    }
}
