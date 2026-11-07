using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Source-only model mapping. No runtime writer or migration is enabled here.
/// Uniqueness is enforced at database level, not by read-before-insert checks.
/// </summary>
public sealed class PayOsPaymentIntentConfiguration : IEntityTypeConfiguration<PayOsPaymentIntent>
{
    public void Configure(EntityTypeBuilder<PayOsPaymentIntent> builder)
    {
        builder.ToTable("BuildingManagementPayOsPaymentIntents");
        builder.ConfigureByConvention();
        builder.HasKey(x => x.Id);

        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.PayerId).IsRequired();
        builder.Property(x => x.OrderCode).IsRequired();
        builder.Property(x => x.AmountVnd).IsRequired();
        builder.Property(x => x.PaymentLinkId).HasMaxLength(128);
        builder.Property(x => x.ProviderReference).HasMaxLength(128);
        builder.Property(x => x.ExpiresAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.ConfirmedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.Status).IsRequired();

        builder.HasIndex(x => x.OrderCode).IsUnique();
        builder.HasIndex(x => x.PaymentLinkId).IsUnique();
        builder.HasIndex(x => x.ProviderReference).IsUnique();
        builder.HasIndex(x => x.PaymentId).IsUnique();
        builder.HasIndex(x => new { x.BuildingId, x.Status });
        builder.HasIndex(x => new { x.BuildingId, x.InvoiceId });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PayOsIntent_AmountPositive", "\"AmountVnd\" > 0");
            t.HasCheckConstraint("CK_PayOsIntent_OrderPositive", "\"OrderCode\" > 0");
            t.HasCheckConstraint("CK_PayOsIntent_ExactlyOneTarget",
                "(\"InvoiceId\" IS NULL) <> (\"PaymentId\" IS NULL)");
        });
    }
}
