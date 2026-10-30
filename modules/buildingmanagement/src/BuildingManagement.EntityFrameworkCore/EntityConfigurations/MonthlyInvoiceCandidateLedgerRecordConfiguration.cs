using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Mapping of UNISSUED monthly financial evidence only. Never exposes
/// InvoiceStatus.Issued, posted debt or permissioned payment allocations.
/// </summary>
public sealed class MonthlyInvoiceCandidateLedgerRecordConfiguration
    : IEntityTypeConfiguration<MonthlyInvoiceCandidateLedgerRecord>
{
    public void Configure(EntityTypeBuilder<MonthlyInvoiceCandidateLedgerRecord> builder)
    {
        builder.ToTable("BuildingManagementMonthlyInvoiceCandidates", table =>
        {
            table.HasCheckConstraint("CK_BM_MonthlyCandidate_Amounts",
                "\"RentAmount\" > 0 AND \"ElectricityAmount\" >= 0 AND " +
                "\"WaterAmount\" >= 0 AND \"AuxiliaryAmount\" >= 0 AND " +
                "\"TotalAmount\" = \"RentAmount\" + \"ElectricityAmount\" + " +
                "\"WaterAmount\" + \"AuxiliaryAmount\"");
            table.HasCheckConstraint("CK_BM_MonthlyCandidate_Period",
                "\"BillingYear\" BETWEEN 2000 AND 2200 AND " +
                "\"BillingMonth\" BETWEEN 1 AND 12");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DraftInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.BillingYear).IsRequired();
        builder.Property(x => x.BillingMonth).IsRequired();
        builder.Property(x => x.ReviewedByUserId).IsRequired();
        builder.Property(x => x.ReviewedAtUtc).IsRequired();
        builder.Property(x => x.DueDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.RentAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.ElectricityAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.WaterAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.AuxiliaryAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.TotalAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.FrozenEvidenceJson).HasColumnType("text").IsRequired();
        builder.Property(x => x.FrozenEvidenceSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.DraftInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_MonthlyCandidate_Draft");
        builder.HasIndex(x => new { x.ContractId, x.BillingYear, x.BillingMonth })
            .IsUnique().HasDatabaseName("UX_BM_MonthlyCandidate_ContractPeriod");
        builder.HasIndex(x => new { x.RoomId, x.BillingYear, x.BillingMonth })
            .IsUnique().HasDatabaseName("UX_BM_MonthlyCandidate_RoomPeriod");
        builder.HasOne<Invoice>().WithMany()
            .HasForeignKey(x => x.DraftInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
