using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class MonthlyInvoicePaymentAllocationRecordConfiguration
    : IEntityTypeConfiguration<MonthlyInvoicePaymentAllocationRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyInvoicePaymentAllocationRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyInvoicePaymentAllocation");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PaymentId).IsRequired();
        builder.Property(x => x.IssuedInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.PaymentNumberSnapshot).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PaidAtUtc).IsRequired();
        builder.Property(x => x.BankTransactionReferenceSnapshot)
            .HasMaxLength(128).IsRequired();
        builder.Property(x => x.RecordedByUserId).IsRequired();
        builder.Property(x => x.RecordedAtUtc).IsRequired();
        builder.HasIndex(x => x.PaymentId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyAllocation_Payment");
        builder.HasIndex(x => new { x.IssuedInvoiceId, x.RecordedAtUtc })
            .HasDatabaseName("IX_BM_MonthlyAllocation_InvoiceDate");
        builder.HasOne<MonthlyIssuedInvoiceRecord>().WithMany()
            .HasForeignKey(x => x.IssuedInvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
