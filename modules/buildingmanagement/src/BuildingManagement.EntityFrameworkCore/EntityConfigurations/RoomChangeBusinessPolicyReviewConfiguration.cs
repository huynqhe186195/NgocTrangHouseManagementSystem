using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeBusinessPolicyReviewConfiguration
    : IEntityTypeConfiguration<RoomChangeBusinessPolicyReviewRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeBusinessPolicyReviewRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeBusinessPolicyReviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.PolicyId).IsRequired();
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.ProposalSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.ReviewedByUserId).IsRequired();
        b.Property(x => x.ReviewedAtUtc).IsRequired();
        b.Property(x => x.Accepted).IsRequired();
        b.Property(x => x.SignedReviewReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.HasOne<RoomChangeBusinessPolicyRecord>().WithMany()
            .HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PolicyId)
            .HasDatabaseName("UX_BM_RoomChangePolicyReview_Policy").IsUnique();
    }
}
