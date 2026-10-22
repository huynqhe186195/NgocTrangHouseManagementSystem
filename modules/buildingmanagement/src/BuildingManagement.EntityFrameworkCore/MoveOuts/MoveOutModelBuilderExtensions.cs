using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using BuildingManagement.MoveOuts;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore;

public static class MoveOutModelBuilderExtensions
{
    public static void ConfigureMoveOutRequests(this ModelBuilder builder)
    {
        builder.Entity<MoveOutRequest>(entity =>
        {
            entity.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "MoveOutRequests",
                BuildingManagementDbProperties.DbSchema);
            entity.ConfigureByConvention();

            entity.Property(x => x.RequestedMoveOutDate)
                .HasColumnType("date").IsRequired();
            entity.Property(x => x.ApprovedMoveOutDate)
                .HasColumnType("date");
            entity.Property(x => x.Status)
                .HasConversion<int>().IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1024);
            entity.Property(x => x.ReviewNotes).HasMaxLength(1024);

            entity.HasOne<Contract>()
                .WithMany().HasForeignKey(x => x.ContractId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Room>()
                .WithMany().HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);

            // Guards duplicate Pending/Approved requests even under concurrency.
            // This SQL filter targets PostgreSQL and SQLite; verify the generated
            // migration when using another provider.
            entity.HasIndex(x => x.ContractId)
                .IsUnique()
                .HasDatabaseName("UX_MoveOutRequests_OneOpenPerContract")
                .HasFilter("\"IsDeleted\" = FALSE AND \"Status\" IN (0, 1)");
        });

        builder.Entity<MoveOutChecklistTask>(entity =>
        {
            entity.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "MoveOutChecklistTasks",
                BuildingManagementDbProperties.DbSchema);
            entity.ConfigureByConvention();
            entity.Property(x => x.Type).HasConversion<int>().IsRequired();
            entity.Property(x => x.IsCompleted).IsRequired();
            entity.HasOne<MoveOutRequest>()
                .WithMany().HasForeignKey(x => x.MoveOutRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.MoveOutRequestId, x.Type })
                .IsUnique().HasDatabaseName("UX_MoveOutChecklistTask_Request_Type");
        });
    }
}
