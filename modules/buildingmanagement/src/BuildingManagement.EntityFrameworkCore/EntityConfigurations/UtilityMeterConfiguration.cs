using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class UtilityMeterConfiguration
        : IEntityTypeConfiguration<UtilityMeter>
    {
        public void Configure(
            EntityTypeBuilder<UtilityMeter> builder)
        {
            builder.ToTable(
                "BuildingManagementUtilityMeters"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.UtilityType)
                .IsRequired();

            builder.Property(x => x.MeterCode)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.InitialReading)
                .HasPrecision(18, 3)
                .IsRequired();

            builder.Property(x => x.InstalledDate)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(x => x.IsActive)
                .IsRequired();

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(x => x.MeterCode)
                .IsUnique();

            // At most one non-deleted ACTIVE meter per room and utility type.
            // Database uniqueness also protects concurrent requests that pass
            // an application-level AnyAsync() check at the same time.
            builder.HasIndex(
                x => new
                {
                    x.RoomId,
                    x.UtilityType
                }
            )
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE");

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}