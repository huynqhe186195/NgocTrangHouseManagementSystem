using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class BuildingPhotoContentConfiguration
        : IEntityTypeConfiguration<BuildingPhotoContent>
    {
        public void Configure(EntityTypeBuilder<BuildingPhotoContent> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "BuildingPhotoContents",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            // The key IS the owning photo's id, so the column is named for
            // that relationship rather than the generic "Id".
            builder.Property(x => x.Id)
                .HasColumnName("PhotoId");

            builder.Property(x => x.Content)
                .IsRequired();

            builder.HasOne<BuildingPhoto>()
                .WithOne()
                .HasForeignKey<BuildingPhotoContent>(x => x.Id)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
