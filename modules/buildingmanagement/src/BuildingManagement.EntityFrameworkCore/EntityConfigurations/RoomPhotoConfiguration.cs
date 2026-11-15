using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class RoomPhotoConfiguration
        : IEntityTypeConfiguration<RoomPhoto>
    {
        public void Configure(EntityTypeBuilder<RoomPhoto> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "RoomPhotos",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.FileName)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(x => x.ContentType)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.SizeBytes)
                .IsRequired();

            builder.Property(x => x.DisplayOrder)
                .IsRequired();

            builder.Property(x => x.BlobName)
                .IsRequired()
                .HasMaxLength(256);

            builder.HasIndex(x => new
            {
                x.RoomId,
                x.DisplayOrder
            });

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
