using BuildingManagement.DepositSettlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class ContractDepositRefundBankReviewRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositRefundBankReviewRecord>
{
    public void Configure(EntityTypeBuilder<ContractDepositRefundBankReviewRecord> b)
    {
        b.ToTable("BuildingManagementContractDepositRefundBankReviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.DepositDecisionId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ReviewedByUserId).IsRequired();
        b.Property(x => x.ReviewedAtUtc).IsRequired();
        b.Property(x => x.PaidAtUtc).IsRequired();
        b.Property(x => x.VerifiedRefundAmountVnd).HasPrecision(18,0).IsRequired();
        b.Property(x => x.BankTransactionReference).HasMaxLength(128).IsRequired();
        b.Property(x => x.BankStatementEvidenceReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.SignedReviewReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.ReviewJson).HasColumnType("text").IsRequired();
        b.Property(x => x.ReviewSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_PostOffsetRefundReview_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_PostOffsetRefundReview_Request");
        b.HasIndex(x => x.BankTransactionReference).IsUnique()
            .HasDatabaseName("UX_BM_PostOffsetRefundReview_BankRef");
    }
}
