using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class MonthlyInvoiceIndependentReviewRecordConfiguration
    : IEntityTypeConfiguration<MonthlyInvoiceIndependentReviewRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyInvoiceIndependentReviewRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyInvoiceReviews");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CandidateId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.CandidateSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PreparedByUserId).IsRequired();
        builder.Property(x => x.ReviewedByUserId).IsRequired();
        builder.Property(x => x.ReviewedAtUtc).IsRequired();
        builder.Property(x => x.Accepted).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.CandidateId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyReview_Candidate");
        builder.HasOne<MonthlyInvoiceCandidateLedgerRecord>().WithMany()
            .HasForeignKey(x => x.CandidateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MonthlyIssuedInvoiceRecordConfiguration
    : IEntityTypeConfiguration<MonthlyIssuedInvoiceRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyIssuedInvoiceRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyIssuedInvoiceLedger");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CandidateId).IsRequired();
        builder.Property(x => x.ReviewId).IsRequired();
        builder.Property(x => x.DraftInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.BillingYear).IsRequired();
        builder.Property(x => x.BillingMonth).IsRequired();
        builder.Property(x => x.InvoiceNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IssuedByUserId).IsRequired();
        builder.Property(x => x.IssuedAtUtc).IsRequired();
        builder.Property(x => x.DueDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.TotalAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.CandidateSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.CandidateId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyIssued_Candidate");
        builder.HasIndex(x => x.ReviewId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyIssued_Review");
        builder.HasIndex(x => x.InvoiceNumber).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyIssued_Number");
        builder.HasIndex(x => new { x.ContractId, x.BillingYear, x.BillingMonth })
            .IsUnique().HasDatabaseName("UX_BM_MonthlyIssued_ContractPeriod");
        builder.HasIndex(x => new { x.RoomId, x.BillingYear, x.BillingMonth })
            .IsUnique().HasDatabaseName("UX_BM_MonthlyIssued_RoomPeriod");
        builder.HasOne<MonthlyInvoiceCandidateLedgerRecord>().WithMany()
            .HasForeignKey(x => x.CandidateId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MonthlyInvoiceIndependentReviewRecord>().WithMany()
            .HasForeignKey(x => x.ReviewId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
