using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeMidMonthCompletionConfiguration
    : IEntityTypeConfiguration<RoomChangeMidMonthCompletionRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeMidMonthCompletionRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeMidMonthCompletions");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceSignedAdjustmentVnd).HasPrecision(18,0);
        b.Property(x => x.SourceAdjustedReceivableVnd).HasPrecision(18,0);
        b.Property(x => x.SourceReviewedReceiptsVnd).HasPrecision(18,0);
        b.Property(x => x.TargetFirstMonthRentVnd).HasPrecision(18,0);
        b.Property(x => x.CarriedDepositVnd).HasPrecision(18,0);
        b.Property(x => x.H4IntentSha256).HasMaxLength(64);
        b.Property(x => x.H4OriginalHeldDepositVnd).HasPrecision(18,0);
        b.Property(x => x.H4TargetRequiredDepositVnd).HasPrecision(18,0);
        b.Property(x => x.H4UnpaidTopUpVnd).HasPrecision(18,0);
        b.Property(x => x.H4TopUpDueDateVietnam).HasColumnType("date");
        b.Property(x => x.H4NextMonthRentCreditVnd).HasPrecision(18,0);
        b.Property(x => x.H4ManualRefundLiabilityVnd).HasPrecision(18,0);
        b.HasIndex(x => x.H4DispositionIntentId)
            .IsUnique().HasFilter("\"H4DispositionIntentId\" IS NOT NULL")
            .HasDatabaseName("UX_BM_2HMidComplete_H4Intent");
        b.Property(x => x.DispositionEvidenceReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.CompletionReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.Ignore(x => x.IsExternalBankPayment);
        b.Ignore(x => x.SupportsRefundOrTopUp);
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.TargetContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId).IsUnique()
            .HasDatabaseName("UX_BM_2HMidComplete_Request");
        b.HasIndex(x => x.SourceContractId).IsUnique()
            .HasDatabaseName("UX_BM_2HMidComplete_Source");
        b.HasIndex(x => x.TargetContractId).IsUnique()
            .HasDatabaseName("UX_BM_2HMidComplete_Target");
        b.HasIndex(x => x.CarryForwardSettlementId).IsUnique()
            .HasDatabaseName("UX_BM_2HMidComplete_Carry");
    }
}
