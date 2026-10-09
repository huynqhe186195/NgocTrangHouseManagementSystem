using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class MeterReadingConfiguration
        : IEntityTypeConfiguration<MeterReading>
    {
        public void Configure(
            EntityTypeBuilder<MeterReading> builder)
        {
            builder.ToTable(
                "BuildingManagementMeterReadings"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.UtilityMeterId)
                .IsRequired();

            builder.Property(x => x.BillingYear)
                .IsRequired();

            builder.Property(x => x.BillingMonth)
                .IsRequired();

            builder.Property(x => x.PreviousReading)
                .HasPrecision(18, 3)
                .IsRequired();

            builder.Property(x => x.CurrentReading)
                .HasPrecision(18, 3)
                .IsRequired();

            builder.Property(x => x.ReadingDate)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(x => new
            {
                x.UtilityMeterId,
                x.BillingYear,
                x.BillingMonth
            })
                .IsUnique();

            builder.HasIndex(x => new
            {
                x.UtilityMeterId,
                x.ReadingDate
            });

            builder.HasOne<UtilityMeter>()
                .WithMany()
                .HasForeignKey(x => x.UtilityMeterId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}