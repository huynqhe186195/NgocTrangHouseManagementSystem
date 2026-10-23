using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class InvoiceItemMeterSourceConfiguration
    : IEntityTypeConfiguration<InvoiceItemMeterSource>
{
    public void Configure(EntityTypeBuilder<InvoiceItemMeterSource> builder)
    {
        builder.ToTable("BuildingManagementInvoiceItemMeterSources");
        builder.ConfigureByConvention();
        builder.Property(x => x.InvoiceItemId).IsRequired();
        builder.Property(x => x.SourceId).IsRequired();
        builder.Property(x => x.UtilityMeterId).IsRequired();
        builder.Property(x => x.SourceType).IsRequired();
        builder.Property(x => x.RecordedDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.PreviousReading).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.CurrentReading).HasPrecision(18, 3).IsRequired();
        builder.Ignore(x => x.Quantity);

        builder.HasIndex(x => new
        {
            x.InvoiceItemId, x.SourceType, x.SourceId
        }).IsUnique().HasDatabaseName("UX_BM_InvoiceSource_Item_Type_Source");

        builder.HasOne<UtilityMeter>().WithMany()
            .HasForeignKey(x => x.UtilityMeterId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
