using BuildingManagement.DepositSettlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class ContractDepositDebtReviewRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositDebtReviewRecord>
{
    public void Configure(EntityTypeBuilder<ContractDepositDebtReviewRecord> b)
    {
        b.ToTable("BuildingManagementContractDepositDebtReviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ReviewedByUserId).IsRequired();
        b.Property(x => x.ReviewedAsOfDate).IsRequired();
        b.Property(x => x.ReviewedAtUtc).IsRequired();
        b.Property(x => x.VerifiedHeldAmount).HasPrecision(18,0).IsRequired();
        b.Property(x => x.VerifiedOutstandingAmount).HasPrecision(18,0).IsRequired();
        b.Property(x => x.SignedReviewReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.ReviewJson).HasColumnType("text").IsRequired();
        b.Property(x => x.ReviewSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetReview_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetReview_Request");
    }
}
