using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Persisted DRAFT ONLY; unique across ALL drafts and any future statuses.
/// No IsDeleted filter: financial identifiers must not be recycled.
/// </summary>
public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("BuildingManagementInvoices");
        builder.ConfigureByConvention();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BillingYear).IsRequired();
        builder.Property(x => x.BillingMonth).IsRequired();
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.ProvisionalUtilityTotal)
            .HasPrecision(18, 3).IsRequired(false);

        builder.HasIndex(x => new
        {
            x.ContractId, x.BillingYear, x.BillingMonth
        }).IsUnique().HasDatabaseName("UX_BM_Invoice_Contract_Period");

        builder.HasIndex(x => new
        {
            x.RoomId, x.BillingYear, x.BillingMonth
        });

        builder.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.ContractId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Room>().WithMany()
            .HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Items).WithOne()
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
