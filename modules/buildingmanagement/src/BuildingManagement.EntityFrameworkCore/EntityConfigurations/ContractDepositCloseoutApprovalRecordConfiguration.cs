using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Historical owner approval, no automatic refund, no debt offset.
/// Actual money workflows remain isolated staging only and require
/// a root Identity lease, contract/building fence and signed bank proof.
/// </summary>
public sealed class ContractDepositCloseoutApprovalRecordConfiguration
    : IEntityTypeConfiguration<ContractDepositCloseoutApprovalRecord>
{
    public void Configure(
        EntityTypeBuilder<ContractDepositCloseoutApprovalRecord> builder)
    {
        builder.ToTable("BuildingManagementContractDepositCloseoutApprovals", t =>
        {
            t.HasCheckConstraint("CK_BM_DepositCloseout_Amounts",
                "\"VerifiedHeldAmount\" > 0 AND " +
                "\"ApprovedRefundAmount\" >= 0 AND " +
                "\"ApprovedForfeitAmount\" >= 0 AND " +
                "\"VerifiedHeldAmount\" = \"ApprovedRefundAmount\" + " +
                "\"ApprovedForfeitAmount\"");
            t.HasCheckConstraint("CK_BM_DepositCloseout_Docs",
                "length(trim(\"InspectionEvidenceReference\")) BETWEEN 8 AND 500 " +
                "AND length(trim(\"HandoverEvidenceReference\")) BETWEEN 8 AND 500 " +
                "AND length(trim(\"ExplicitDepositPolicyReference\")) BETWEEN 8 AND 200");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.MoveOutRequestId).IsRequired();
        builder.Property(x => x.IssuedInvoiceId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.ApprovedByUserId).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).IsRequired();
        builder.Property(x => x.VerifiedHeldAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.ApprovedForfeitAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.ApprovedRefundAmount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.RefundSettlementId).IsRequired(false);
        builder.Property(x => x.ForfeitSettlementId).IsRequired(false);
        builder.Property(x => x.RefundTransactionReference).HasMaxLength(128);
        builder.Property(x => x.RefundPaidAtUtc).IsRequired(false);
        builder.Property(x => x.DamageReason).HasMaxLength(500);
        builder.Property(x => x.DamageEvidenceReference).HasMaxLength(500);
        builder.Property(x => x.InspectionEvidenceReference)
            .HasMaxLength(500).IsRequired();
        builder.Property(x => x.HandoverEvidenceReference)
            .HasMaxLength(500).IsRequired();
        builder.Property(x => x.ExplicitDepositPolicyReference)
            .HasMaxLength(200).IsRequired();
        builder.Property(x => x.ApprovalJson).HasColumnType("text").IsRequired();
        builder.Property(x => x.ApprovalSha256).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_DepositCloseout_Contract");
        builder.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_DepositCloseout_Request");
        builder.HasIndex(x => x.IssuedInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_DepositCloseout_IssuedInvoice");
        builder.HasIndex(x => x.RefundSettlementId).IsUnique()
            .HasDatabaseName("UX_BM_DepositCloseout_Refund");
        builder.HasIndex(x => x.ForfeitSettlementId).IsUnique()
            .HasDatabaseName("UX_BM_DepositCloseout_Forfeit");

        builder.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.ContractId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MoveOutRequest>().WithMany()
            .HasForeignKey(x => x.MoveOutRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IssuedInvoiceLedgerRecord>().WithMany()
            .HasForeignKey(x => x.IssuedInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DepositSettlement>().WithMany()
            .HasForeignKey(x => x.RefundSettlementId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DepositSettlement>().WithMany()
            .HasForeignKey(x => x.ForfeitSettlementId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
