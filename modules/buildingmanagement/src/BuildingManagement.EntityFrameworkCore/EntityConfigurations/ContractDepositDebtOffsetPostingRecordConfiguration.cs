using BuildingManagement.DepositSettlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class ContractDepositDebtOffsetPostingRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositDebtOffsetPostingRecord>
{
    public void Configure(EntityTypeBuilder<ContractDepositDebtOffsetPostingRecord> b)
    {
        b.ToTable("BuildingManagementContractDepositDebtOffsetPostings");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ApprovalId).IsRequired();
        b.Property(x => x.PostedByOwnerId).IsRequired();
        b.Property(x => x.PostedAtUtc).IsRequired();
        b.Property(x => x.VerifiedHeldAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.DebitHeldDepositVnd).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.CreditOfficialDebtVnd).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.RemainingHeldDepositVnd).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.RemainingOfficialDebtVnd).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ApprovalSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.PostingJson).HasColumnType("text").IsRequired();
        b.Property(x => x.PostingSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetPosted_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetPosted_Request");
        b.HasIndex(x => x.ApprovalId).IsUnique()
            .HasDatabaseName("UX_BM_OffsetPosted_Approval");
    }
}
