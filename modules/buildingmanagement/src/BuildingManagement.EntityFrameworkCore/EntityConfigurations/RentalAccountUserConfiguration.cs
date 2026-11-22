using BuildingManagement.RentalAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public class RentalAccountUserConfiguration : IEntityTypeConfiguration<RentalAccountUser>
{
    public void Configure(EntityTypeBuilder<RentalAccountUser> builder)
    {
        builder.ToTable("BuildingManagementRentalAccountUsers");
        builder.ConfigureByConvention();

        builder.Property(x => x.IdentityUserId)
            .IsRequired();

        builder.Property(x => x.TenantId)
            .IsRequired(false);

        builder.Property(x => x.AccessRole)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.Property(x => x.RevokedAt)
            .IsRequired(false);

        builder.HasIndex(x => new { x.RentalAccountId, x.IdentityUserId })
            .IsUnique();

        builder.HasIndex(x => x.IdentityUserId);
        builder.HasIndex(x => new { x.RentalAccountId, x.IsActive });
        builder.HasIndex(x => x.TenantId);

        builder.HasOne<RentalAccount>()
            .WithMany()
            .HasForeignKey(x => x.RentalAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
