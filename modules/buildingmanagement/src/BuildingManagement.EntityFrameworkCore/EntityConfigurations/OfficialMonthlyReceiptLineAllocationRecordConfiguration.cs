using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Staged receipt lines; DB constraints and deferred triggers enforce exact
/// receipt completeness, while domain evaluator verifies archived tariffs.
/// </summary>
public sealed class OfficialMonthlyReceiptLineAllocationRecordConfiguration
    : IEntityTypeConfiguration<OfficialMonthlyReceiptLineAllocationRecord>
{
    public void Configure(EntityTypeBuilder<OfficialMonthlyReceiptLineAllocationRecord> b)
    {
        b.ToTable("BuildingManagementOfficialMonthlyReceiptLineAllocations");
        b.HasKey(x => x.Id);
        b.Property(x => x.ReceivablePostingId).IsRequired();
        b.Property(x => x.ReceiptReviewId).IsRequired();
        b.Property(x => x.ReceiptAllocationId).IsRequired();
        b.Property(x => x.PaymentId).IsRequired();
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.Category).IsRequired();
        b.Property(x => x.Amount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.AllocatedByUserId).IsRequired();
        b.Property(x => x.AllocatedAtUtc).IsRequired();
        b.Property(x => x.SignedAllocationReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.InvoiceEvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.ReceiptReviewId, x.Category }).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyReceiptLine_ReviewCategory");
        b.HasIndex(x => new { x.ReceivablePostingId, x.Category })
            .HasDatabaseName("IX_BM_MonthlyReceiptLine_InvoiceCategory");
        b.HasOne<OfficialInvoiceReceivablePostingRecord>().WithMany()
            .HasForeignKey(x => x.ReceivablePostingId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OfficialInvoiceReceiptReviewRecord>().WithMany()
            .HasForeignKey(x => x.ReceiptReviewId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
