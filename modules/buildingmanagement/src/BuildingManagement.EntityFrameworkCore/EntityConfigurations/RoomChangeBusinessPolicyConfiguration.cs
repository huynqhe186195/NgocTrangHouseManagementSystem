using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeBusinessPolicyConfiguration
    : IEntityTypeConfiguration<RoomChangeBusinessPolicyRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeBusinessPolicyRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeBusinessPolicies");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.RosterSnapshotId).IsRequired();
        b.Property(x => x.RosterSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.ContractTreatment).HasConversion<int>().IsRequired();
        b.Property(x => x.PricingTreatment).HasConversion<int>().IsRequired();
        b.Property(x => x.DepositTreatment).HasConversion<int>().IsRequired();
        b.Property(x => x.TenantTreatment).HasConversion<int>().IsRequired();
        b.Property(x => x.ContractPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.PricingPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.DepositPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.TenantConsentReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.ProposedByUserId).IsRequired();
        b.Property(x => x.ProposedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomChangeTenantRosterSnapshotRecord>().WithMany()
            .HasForeignKey(x => x.RosterSnapshotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId)
            .HasDatabaseName("UX_BM_RoomChangePolicy_Request").IsUnique();
    }
}
