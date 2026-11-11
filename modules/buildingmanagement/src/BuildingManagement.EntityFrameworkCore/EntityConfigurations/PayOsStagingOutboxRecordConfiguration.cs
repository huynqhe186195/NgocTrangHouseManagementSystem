using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Staged outbox schema only (isolated payOS staging lane). One pending row
/// per intent and kind prevents duplicate work; the unique composite index
/// backs the same "never queue the same follow-up twice" rule the inbox
/// already enforces for provider references.
/// </summary>
public sealed class PayOsStagingOutboxRecordConfiguration :
    IEntityTypeConfiguration<PayOsStagingOutboxRecord>
{
    public void Configure(EntityTypeBuilder<PayOsStagingOutboxRecord> builder)
    {
        builder.ToTable("BuildingManagementPayOsStagingOutbox");
        builder.ConfigureByConvention();
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PaymentIntentId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.OrderCode).IsRequired();
        builder.Property(x => x.Kind).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.CreatedAtUtc)
            .HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.CompletedAtUtc)
            .HasColumnType("timestamp with time zone");
        builder.Property(x => x.NextAttemptAtUtc)
            .HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.LastAttemptedAtUtc)
            .HasColumnType("timestamp with time zone");
        builder.Property(x => x.LastObservedProviderStatus).HasMaxLength(32);

        builder.HasIndex(x => new { x.PaymentIntentId, x.Kind }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        builder.HasIndex(x => new { x.BuildingId, x.Status });
        builder.HasOne<PayOsPaymentIntent>().WithMany()
            .HasForeignKey(x => new { x.PaymentIntentId, x.BuildingId, x.OrderCode })
            .HasPrincipalKey(x => new { x.Id, x.BuildingId, x.OrderCode })
            .OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PayOsOutbox_OrderCodePositive",
                "\"OrderCode\" > 0");
            t.HasCheckConstraint("CK_PayOsOutbox_CompletedOnlyWhenCompleted",
                "(\"Status\" = 1 AND \"CompletedAtUtc\" IS NOT NULL) OR " +
                "(\"Status\" <> 1 AND \"CompletedAtUtc\" IS NULL)");
        });
    }
}
