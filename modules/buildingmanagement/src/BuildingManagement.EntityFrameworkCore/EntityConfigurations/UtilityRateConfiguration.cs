using BuildingManagement.Buildings;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class UtilityRateConfiguration
        : IEntityTypeConfiguration<UtilityRate>
    {
        public void Configure(
            EntityTypeBuilder<UtilityRate> builder)
        {
            builder.ToTable(
                "BuildingManagementUtilityRates"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.BuildingId)
                .IsRequired();

            builder.Property(x => x.UtilityType)
                .IsRequired();

            builder.Property(x => x.UnitPrice)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.EffectiveFrom)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(x => x.EffectiveTo)
                .HasColumnType("date");

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(
                x => new
                {
                    x.BuildingId,
                    x.UtilityType
                }
            );

            builder.HasIndex(
                    x => new
                    {
                        x.BuildingId,
                        x.UtilityType,
                        x.EffectiveFrom
                    }
                )
                .IsUnique();

            builder.HasOne<Building>()
                .WithMany()
                .HasForeignKey(x => x.BuildingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}