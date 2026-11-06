using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Staged schema only. A future inbox writer must lock the intent and insert
/// this row in a transaction; the unique indexes prevent concurrent duplicates.
/// </summary>
public sealed class PayOsWebhookInboxRecordConfiguration : IEntityTypeConfiguration<PayOsWebhookInboxRecord>
{
    public void Configure(EntityTypeBuilder<PayOsWebhookInboxRecord> builder)
    {
        builder.ToTable("BuildingManagementPayOsWebhookInbox");
        builder.ConfigureByConvention();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.PaymentIntentId).IsRequired();
        builder.Property(x => x.OrderCode).IsRequired();
        builder.Property(x => x.ProviderReference).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PayloadSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ReceivedAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.ReviewedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(x => x.Status).IsRequired();
        // One reference can never be credited twice, even across buildings.
        builder.HasIndex(x => x.ProviderReference).IsUnique();
        builder.HasIndex(x => new { x.PaymentIntentId, x.ProviderReference }).IsUnique();
        builder.HasIndex(x => new { x.BuildingId, x.Status, x.ReceivedAtUtc });
        builder.HasIndex(x => x.OrderCode);
        builder.HasOne<PayOsPaymentIntent>().WithMany()
            .HasForeignKey(x => x.PaymentIntentId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PayOsInbox_OrderCodePositive", "\"OrderCode\" > 0");
            t.HasCheckConstraint("CK_PayOsInbox_Digest64", "length(\"PayloadSha256\") = 64");
        });
    }
}
