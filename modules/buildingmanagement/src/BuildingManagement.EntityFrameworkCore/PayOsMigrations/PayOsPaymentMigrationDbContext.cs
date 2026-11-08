using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.PayOsMigrations;

/// <summary>
/// Migration-only bounded context for the two payOS staging tables.
///
/// The legacy BuildingManagementDbContext snapshot includes intentionally staged
/// non-payment entities. Its baseline is NOT safe for a payment-only migration.
/// Both contexts apply the exact SAME EF configurations; this class is not
/// registered as a runtime DbContext, and has no public financial writer.
/// </summary>
public sealed class PayOsPaymentMigrationDbContext : DbContext
{
    public DbSet<PayOsPaymentIntent> PayOsPaymentIntents => Set<PayOsPaymentIntent>();
    public DbSet<PayOsWebhookInboxRecord> PayOsWebhookInbox => Set<PayOsWebhookInboxRecord>();

    public PayOsPaymentMigrationDbContext(
        DbContextOptions<PayOsPaymentMigrationDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new PayOsPaymentIntentConfiguration());
        builder.ApplyConfiguration(new PayOsWebhookInboxRecordConfiguration());
    }
}
