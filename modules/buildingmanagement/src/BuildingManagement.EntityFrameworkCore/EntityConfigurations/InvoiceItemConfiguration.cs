using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class InvoiceItemConfiguration : IEntityTypeConfiguration<InvoiceItem>
{
    public void Configure(EntityTypeBuilder<InvoiceItem> builder)
    {
        builder.ToTable("BuildingManagementInvoiceItems");
        builder.ConfigureByConvention();

        builder.Property(x => x.InvoiceId).IsRequired();
        builder.Property(x => x.UtilityType).IsRequired();
        builder.Property(x => x.UtilityRateId).IsRequired(false);
        builder.Property(x => x.RateStatus).IsRequired().HasMaxLength(64);
        builder.Property(x => x.RecordedQuantity).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0).IsRequired(false);
        builder.Property(x => x.ProvisionalAmount).HasPrecision(18, 3).IsRequired(false);
        builder.Property(x => x.CoverageIssuesJson).IsRequired().HasColumnType("text");

        builder.HasIndex(x => new { x.InvoiceId, x.UtilityType })
            .IsUnique().HasDatabaseName("UX_BM_InvoiceItem_Invoice_Utility");

        builder.HasOne<UtilityRate>().WithMany()
            .HasForeignKey(x => x.UtilityRateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.MeterSources).WithOne()
            .HasForeignKey(x => x.InvoiceItemId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.MeterSources)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
