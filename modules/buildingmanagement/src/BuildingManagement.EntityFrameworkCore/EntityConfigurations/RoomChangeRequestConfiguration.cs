using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeRequestConfiguration : IEntityTypeConfiguration<RoomChangeRequest>
{
    public void Configure(EntityTypeBuilder<RoomChangeRequest> b)
    {
        b.ToTable("BuildingManagementRoomChangeRequests");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.SourceRoomId).IsRequired();
        b.Property(x => x.TargetRoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.RequestedDate).HasColumnType("date").IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).HasConversion<int>().IsRequired();
        b.Property(x => x.RequestedByUserId).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.ReviewedByUserId).IsRequired(false);
        b.Property(x => x.ReviewedAtUtc).IsRequired(false);
        b.Property(x => x.ReviewNotes).HasMaxLength(500).IsRequired(false);
        b.HasOne<Contract>().WithMany().HasForeignKey(x => x.SourceContractId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.SourceRoomId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.TargetRoomId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SourceContractId)
            .HasDatabaseName("UX_BM_RoomChangeRequest_OneOpenPerContract")
            .IsUnique().HasFilter("\"Status\" IN (0,1)");
        b.HasIndex(x => new { x.BuildingId, x.TargetRoomId, x.RequestedDate })
            .HasDatabaseName("IX_BM_RoomChangeRequest_TargetDate");
    }
}
