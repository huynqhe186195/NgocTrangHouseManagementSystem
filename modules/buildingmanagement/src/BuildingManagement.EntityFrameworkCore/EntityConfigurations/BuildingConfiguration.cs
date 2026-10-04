using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public class BuildingConfiguration
    : IEntityTypeConfiguration<Building>
{
    public void Configure(
        EntityTypeBuilder<Building> builder)
    {
        builder.ToTable("Buildings");

        builder.ConfigureByConvention(); // this code will help abp auto configuration properties another

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.Address)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.Description)
            .HasMaxLength(500);
    }
}