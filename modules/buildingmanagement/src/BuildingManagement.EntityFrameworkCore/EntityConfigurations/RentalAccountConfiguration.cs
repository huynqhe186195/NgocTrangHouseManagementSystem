using BuildingManagement.RentalAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public class RentalAccountConfiguration : IEntityTypeConfiguration<RentalAccount>
{
    public void Configure(EntityTypeBuilder<RentalAccount> builder)
    {
        builder.ToTable("BuildingManagementRentalAccounts");
        builder.ConfigureByConvention();

        builder.Property(x => x.AccountNumber)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.HasIndex(x => x.AccountNumber)
            .IsUnique();

        builder.HasIndex(x => x.IsActive);
    }
}
