using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class BuildingPhotoConfiguration
        : IEntityTypeConfiguration<BuildingPhoto>
    {
        public void Configure(EntityTypeBuilder<BuildingPhoto> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "BuildingPhotos",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.BuildingId)
                .IsRequired();

            builder.Property(x => x.FileName)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(x => x.ContentType)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.SizeBytes)
                .IsRequired();

            // One cover image per building: upload replaces, never accumulates.
            // Filtered on IsDeleted, otherwise a deleted cover would keep the
            // building from ever accepting a new one.
            builder.HasIndex(x => x.BuildingId)
                .IsUnique()
                .HasFilter("\"IsDeleted\" = FALSE");

            builder.HasOne<Building>()
                .WithMany()
                .HasForeignKey(x => x.BuildingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
