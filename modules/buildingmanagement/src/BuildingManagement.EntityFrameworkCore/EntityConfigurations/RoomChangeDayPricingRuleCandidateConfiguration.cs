using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeDayPricingRuleCandidateConfiguration
    : IEntityTypeConfiguration<RoomChangeDayPricingRuleCandidate>
{
    public void Configure(EntityTypeBuilder<RoomChangeDayPricingRuleCandidate> b)
    {
        b.ToTable("BuildingManagementRoomChangeDayPricingRuleCandidates");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.SourceRoomId).IsRequired();
        b.Property(x => x.TargetRoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.RosterSnapshotId).IsRequired();
        b.Property(x => x.PolicyId).IsRequired();
        b.Property(x => x.PolicyReviewId).IsRequired();
        b.Property(x => x.TransferDate).IsRequired();
        b.Property(x => x.TransferDayOwner).HasConversion<int>().IsRequired();
        b.Property(x => x.Rounding).HasConversion<int>().IsRequired();
        b.Property(x => x.CalendarDays).IsRequired();
        b.Property(x => x.SourceChargeableDays).IsRequired();
        b.Property(x => x.TargetChargeableDays).IsRequired();
        b.Property(x => x.SourceMonthlyRentVnd).HasPrecision(18,0).IsRequired();
        b.Property(x => x.TargetMonthlyRentVnd).HasPrecision(18,0).IsRequired();
        b.Property(x => x.ProposedSourceRentVnd).HasPrecision(18,0).IsRequired();
        b.Property(x => x.ProposedTargetRentVnd).HasPrecision(18,0).IsRequired();
        b.Property(x => x.SignedPricingRuleReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomChangeTenantRosterSnapshotRecord>().WithMany()
            .HasForeignKey(x => x.RosterSnapshotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomChangeBusinessPolicyRecord>().WithMany()
            .HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomChangeBusinessPolicyReviewRecord>().WithMany()
            .HasForeignKey(x => x.PolicyReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId)
            .HasDatabaseName("UX_BM_RoomChangeDayPricingRule_Request").IsUnique();
    }
}
