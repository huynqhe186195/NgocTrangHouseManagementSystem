using BuildingManagement.Buildings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations;

/// <summary>
/// One published-info row per building, enforced by a unique index rather
/// than by trusting the app service to check first.
/// </summary>
public class BuildingPublicInfoConfiguration
    : IEntityTypeConfiguration<BuildingPublicInfo>
{
    public void Configure(EntityTypeBuilder<BuildingPublicInfo> builder)
    {
        builder.ToTable(
            BuildingManagementDbProperties.DbTablePrefix + "BuildingPublicInfos",
            BuildingManagementDbProperties.DbSchema);

        builder.ConfigureByConvention();

        builder.Property(x => x.BuildingId).IsRequired();

        builder.Property(x => x.ElectricityUnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.WaterUnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.ServiceFee).HasPrecision(18, 0);

        builder.Property(x => x.OtherInfo).HasMaxLength(1000);

        builder.Property(x => x.HouseRulesJson).HasMaxLength(4000);

        builder.HasIndex(x => x.BuildingId).IsUnique();

        builder.HasOne<Building>()
            .WithMany()
            .HasForeignKey(x => x.BuildingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
