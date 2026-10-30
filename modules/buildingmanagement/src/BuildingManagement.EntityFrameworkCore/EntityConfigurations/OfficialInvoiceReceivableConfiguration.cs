using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class OfficialInvoiceReceivablePostingRecordConfiguration
    : IEntityTypeConfiguration<OfficialInvoiceReceivablePostingRecord>
{
    public void Configure(EntityTypeBuilder<OfficialInvoiceReceivablePostingRecord> b)
    {
        b.ToTable("BuildingManagementOfficialInvoiceReceivables");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).IsRequired();
        b.Property(x => x.IssuedInvoiceId).IsRequired();
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.RoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.BillingYear).IsRequired();
        b.Property(x => x.BillingMonth).IsRequired();
        b.Property(x => x.OriginalAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.DueDate).HasColumnType("date").IsRequired();
        b.Property(x => x.SourceEvidenceSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.PostedByUserId).IsRequired();
        b.Property(x => x.PostedAtUtc).IsRequired();
        b.HasIndex(x => new { x.Kind, x.IssuedInvoiceId }).IsUnique()
            .HasDatabaseName("UX_BM_OfficialReceivable_Issued");
        b.HasIndex(x => new { x.RoomId, x.BillingYear, x.BillingMonth })
            .IsUnique().HasDatabaseName("UX_BM_OfficialReceivable_RoomPeriod");
    }
}

public sealed class OfficialInvoiceReceiptReviewRecordConfiguration
    : IEntityTypeConfiguration<OfficialInvoiceReceiptReviewRecord>
{
    public void Configure(EntityTypeBuilder<OfficialInvoiceReceiptReviewRecord> b)
    {
        b.ToTable("BuildingManagementOfficialInvoiceReceiptReviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.ReceivablePostingId).IsRequired();
        b.Property(x => x.ReceiptAllocationId).IsRequired();
        b.Property(x => x.PaymentId).IsRequired();
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.Amount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.BankTransactionReference).HasMaxLength(128).IsRequired();
        b.Property(x => x.BankStatementEvidenceReference).HasMaxLength(200).IsRequired();
        b.Property(x => x.ReviewedByUserId).IsRequired();
        b.Property(x => x.ReviewedAtUtc).IsRequired();
        b.Property(x => x.Accepted).IsRequired();
        b.Property(x => x.ReviewReason).HasMaxLength(500).IsRequired();
        b.HasIndex(x => new { x.ReceivablePostingId, x.ReceiptAllocationId })
            .IsUnique().HasDatabaseName("UX_BM_OfficialReceipt_Allocation");
        b.HasIndex(x => x.PaymentId).IsUnique()
            .HasDatabaseName("UX_BM_OfficialReceipt_Payment");
        b.HasOne<OfficialInvoiceReceivablePostingRecord>().WithMany()
            .HasForeignKey(x => x.ReceivablePostingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
