using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class MonthlyInvoiceAdjustmentRecordConfiguration
    : IEntityTypeConfiguration<MonthlyInvoiceAdjustmentRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyInvoiceAdjustmentRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyInvoiceAdjustments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IssuedInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.InvoiceEvidenceSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SignedDeltaAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SourceEvidenceReference).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProposedByUserId).IsRequired();
        builder.Property(x => x.ProposedAtUtc).IsRequired();
        builder.HasIndex(x => new { x.IssuedInvoiceId, x.SourceEvidenceReference })
            .IsUnique().HasDatabaseName("UX_BM_MonthlyAdjustment_Evidence");
        builder.HasOne<MonthlyIssuedInvoiceRecord>().WithMany()
            .HasForeignKey(x => x.IssuedInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MonthlyInvoiceAdjustmentDecisionRecordConfiguration
    : IEntityTypeConfiguration<MonthlyInvoiceAdjustmentDecisionRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyInvoiceAdjustmentDecisionRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyInvoiceAdjustmentDecisions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AdjustmentId).IsRequired();
        builder.Property(x => x.IssuedInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.ApprovedSignedDeltaAmount)
            .HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.ReviewedByUserId).IsRequired();
        builder.Property(x => x.ReviewedAtUtc).IsRequired();
        builder.Property(x => x.Accepted).IsRequired();
        builder.Property(x => x.ReviewReason).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.AdjustmentId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyAdjustment_Decision");
        builder.HasOne<MonthlyInvoiceAdjustmentRecord>().WithMany()
            .HasForeignKey(x => x.AdjustmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
