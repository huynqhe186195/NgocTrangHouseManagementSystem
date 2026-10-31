using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeTenantRosterSnapshotConfiguration
    : IEntityTypeConfiguration<RoomChangeTenantRosterSnapshotRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeTenantRosterSnapshotRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeTenantRosterSnapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.RoomChangeRequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.SourceRoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.CapturedByUserId).IsRequired();
        b.Property(x => x.CapturedAtUtc).IsRequired();
        b.Property(x => x.RosterCount).IsRequired();
        b.Property(x => x.RosterJson).HasColumnType("text").IsRequired();
        b.Property(x => x.RosterSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RoomChangeRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RoomChangeRequestId).IsUnique()
            .HasDatabaseName("UX_BM_RoomChangeTenantRoster_Request");
    }
}
