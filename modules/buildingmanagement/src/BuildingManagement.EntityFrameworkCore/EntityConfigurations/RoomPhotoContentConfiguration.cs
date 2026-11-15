using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class RoomPhotoContentConfiguration
        : IEntityTypeConfiguration<RoomPhotoContent>
    {
        public void Configure(EntityTypeBuilder<RoomPhotoContent> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "RoomPhotoContents",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            // The key IS the owning photo's id, so the column is named for
            // that relationship rather than the generic "Id".
            builder.Property(x => x.Id)
                .HasColumnName("PhotoId");

            builder.Property(x => x.Content)
                .IsRequired();

            builder.HasOne<RoomPhoto>()
                .WithOne()
                .HasForeignKey<RoomPhotoContent>(x => x.Id)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
