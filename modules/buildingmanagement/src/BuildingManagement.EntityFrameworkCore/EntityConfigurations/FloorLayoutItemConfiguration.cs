using BuildingManagement.Floors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class FloorLayoutItemConfiguration
        : IEntityTypeConfiguration<FloorLayoutItem>
    {
        public void Configure(EntityTypeBuilder<FloorLayoutItem> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "FloorLayoutItems",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.FloorId)
                .IsRequired();

            builder.Property(x => x.Kind)
                .IsRequired();

            builder.Property(x => x.Label)
                .HasMaxLength(128);

            builder.Property(x => x.X)
                .HasPrecision(10, 2)
                .IsRequired();

            builder.Property(x => x.Y)
                .HasPrecision(10, 2)
                .IsRequired();

            builder.Property(x => x.Width)
                .HasPrecision(10, 2)
                .IsRequired();

            builder.Property(x => x.Height)
                .HasPrecision(10, 2)
                .IsRequired();

            builder.HasIndex(x => x.FloorId);

            builder.HasOne<Floor>()
                .WithMany()
                .HasForeignKey(x => x.FloorId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
