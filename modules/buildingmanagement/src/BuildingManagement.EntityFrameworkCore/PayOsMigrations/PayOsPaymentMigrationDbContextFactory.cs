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

        var connection = disposable
            ? "Host=127.0.0.1;Port=5432;Database=payos_ef_migration_test;Username=postgres;Password=" + password + ";Timeout=3"
            : "Host=127.0.0.1;Port=1;Database=payos_offline_scaffold;Username=offline;Password=offline-only;Timeout=1";

        var options = new DbContextOptionsBuilder<PayOsPaymentMigrationDbContext>()
            .UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_PayOs"))
            .Options;
        return new PayOsPaymentMigrationDbContext(options);
    }
}
