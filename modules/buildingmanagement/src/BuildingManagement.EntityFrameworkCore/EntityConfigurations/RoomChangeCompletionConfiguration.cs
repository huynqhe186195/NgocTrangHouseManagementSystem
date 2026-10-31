using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeCompletionConfiguration
    : IEntityTypeConfiguration<RoomChangeCompletionRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeCompletionRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeCompletions");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.TargetContractId).IsRequired();
        b.Property(x => x.SourceRoomId).IsRequired();
        b.Property(x => x.TargetRoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.PolicyId).IsRequired();
        b.Property(x => x.PolicyReviewId).IsRequired();
        b.Property(x => x.RosterSnapshotId).IsRequired();
        b.Property(x => x.HandoverEvidenceId).IsRequired();
        b.Property(x => x.CarryForwardSettlementId).IsRequired();
        b.Property(x => x.CarriedDepositAmount).HasPrecision(18,0).IsRequired();
        b.Property(x => x.SourceRoomDisposition).HasConversion<int>().IsRequired();
        b.Property(x => x.CompletedByUserId).IsRequired();
        b.Property(x => x.CompletedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId).IsUnique();
        b.HasIndex(x => x.SourceContractId).IsUnique();
        b.HasIndex(x => x.TargetContractId).IsUnique();
        b.HasIndex(x => x.CarryForwardSettlementId).IsUnique();
    }
}
