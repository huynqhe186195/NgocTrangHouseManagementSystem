using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Staging evidence only; the TARGET new contract may not yet exist at
/// planning time. No transfer, receivable recognition, or bank posting.
/// </summary>
public sealed class RoomChangeDepositDispositionIntentConfiguration
    : IEntityTypeConfiguration<RoomChangeDepositDispositionIntentRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeDepositDispositionIntentRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeDepositDispositionIntents");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.TargetContractId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.TransferDateVietnam).HasColumnType("date").IsRequired();
        b.Property(x => x.ClaimedOriginalSourceHeldVnd).HasPrecision(18,0);
        b.Property(x => x.AlreadyPostedApprovedDeductionsVnd).HasPrecision(18,0);
        b.Property(x => x.ProposedCarryTargetVnd).HasPrecision(18,0);
        b.Property(x => x.TargetRequiredDepositVnd).HasPrecision(18,0);
        b.Property(x => x.ProposedUnpaidTopUpVnd).HasPrecision(18,0);
        b.Property(x => x.ProposedTopUpDueDateVietnam).HasColumnType("date");
        b.Property(x => x.ProposedFutureRentCreditVnd).HasPrecision(18,0);
        b.Property(x => x.ProposedManualRefundVnd).HasPrecision(18,0);
        b.Property(x => x.SurplusChoice).HasConversion<int>().IsRequired();
        b.Property(x => x.OriginalCashLedgerReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.TargetDepositPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.SignedDeductionReference).HasMaxLength(500);
        b.Property(x => x.OwnerSignedDispositionReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.ProposedByOwnerId).IsRequired();
        b.Property(x => x.ProposedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.RequestId)
            .HasDatabaseName("UX_BM_H4Intent_Request").IsUnique();
        b.HasIndex(x=>new{x.BuildingId,x.SourceContractId,x.TransferDateVietnam})
            .HasDatabaseName("IX_BM_H4Intent_BuildingSourceDate");
    }
}
