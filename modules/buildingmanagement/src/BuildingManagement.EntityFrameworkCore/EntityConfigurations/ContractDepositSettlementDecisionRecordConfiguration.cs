using BuildingManagement.DepositSettlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class ContractDepositSettlementDecisionRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositSettlementDecisionRecord>
{
    public void Configure(EntityTypeBuilder<ContractDepositSettlementDecisionRecord> b)
    {
        b.ToTable("BuildingManagementContractDepositSettlementDecisions");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.FinalIssuedInvoiceId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ApprovedByUserId).IsRequired();
        b.Property(x => x.ApprovedAtUtc).IsRequired();
        b.Property(x => x.VerifiedHeldAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ApprovedForfeitAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ApprovedRefundAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.ApprovedDebtOffsetAmount).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.DamageReason).HasMaxLength(500);
        b.Property(x => x.DamageEvidenceReference).HasMaxLength(500);
        b.Property(x => x.ExplicitDepositPolicyReference).HasMaxLength(200).IsRequired();
        b.Property(x => x.ApprovalJson).HasColumnType("text").IsRequired();
        b.Property(x => x.ApprovalSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_DepositDecision_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_DepositDecision_MoveOut");
        b.HasIndex(x => x.FinalIssuedInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_DepositDecision_FinalInvoice");
    }
}
