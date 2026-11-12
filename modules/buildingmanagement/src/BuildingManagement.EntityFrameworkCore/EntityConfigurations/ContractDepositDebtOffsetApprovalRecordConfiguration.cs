using BuildingManagement.DepositSettlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class ContractDepositDebtOffsetApprovalRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositDebtOffsetApprovalRecord>
{
    public void Configure(EntityTypeBuilder<ContractDepositDebtOffsetApprovalRecord> b)
    {
        b.ToTable("BuildingManagementContractDepositDebtOffsetApprovals");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ApprovedByOwnerId).IsRequired();
        b.Property(x => x.IndependentDebtReviewerId).IsRequired();
        b.Property(x => x.ApprovedAtUtc).IsRequired();
        b.Property(x => x.VerifiedHeldAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ApprovedOffsetAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ExpectedRemainingDeposit).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ExpectedUncoveredDebt).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.SignedPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.IndependentReviewReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.ApprovalJson).HasColumnType("text").IsRequired();
        b.Property(x => x.ApprovalSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetApproval_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetApproval_Request");
    }
}
