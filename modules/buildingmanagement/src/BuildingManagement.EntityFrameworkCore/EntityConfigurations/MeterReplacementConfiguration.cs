using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class MeterReplacementConfiguration : IEntityTypeConfiguration<MeterReplacement>
{
    public void Configure(EntityTypeBuilder<MeterReplacement> builder)
    {
        builder.ToTable("BuildingManagementMeterReplacements");
        builder.ConfigureByConvention();
        builder.Property(x => x.OldMeterId).IsRequired();
        builder.Property(x => x.NewMeterId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.UtilityType).IsRequired();
        builder.Property(x => x.ReplacementDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.OldPreviousReading).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.OldFinalReading).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.NewInitialReading).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(500);
        // A physical meter may participate at most once on each side.
        builder.HasIndex(x => x.OldMeterId).IsUnique();
        builder.HasIndex(x => x.NewMeterId).IsUnique();
        builder.HasIndex(x => new { x.RoomId, x.ReplacementDate });
        builder.HasOne<UtilityMeter>().WithMany().HasForeignKey(x => x.OldMeterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_BM_MeterReplacements_OldMeter");
        builder.HasOne<UtilityMeter>().WithMany().HasForeignKey(x => x.NewMeterId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_BM_MeterReplacements_NewMeter");
        builder.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_BM_MeterReplacements_Room");
    }
}
