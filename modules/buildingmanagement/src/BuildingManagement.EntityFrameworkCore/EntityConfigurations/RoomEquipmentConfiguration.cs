using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class RoomEquipmentConfiguration
        : IEntityTypeConfiguration<RoomEquipment>
    {
        public void Configure(EntityTypeBuilder<RoomEquipment> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "RoomEquipment",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(128);

            builder.Property(x => x.Category)
                .HasMaxLength(64);

            builder.Property(x => x.Quantity)
                .IsRequired();

            builder.Property(x => x.Condition)
                .IsRequired();

            builder.Property(x => x.InstalledDate)
                .HasColumnType("date")
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(x => x.RoomId);

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
