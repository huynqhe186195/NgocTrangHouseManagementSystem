using BuildingManagement.Contracts;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class ContractTenantConfiguration
        : IEntityTypeConfiguration<ContractTenant>
    {
        public void Configure(
            EntityTypeBuilder<ContractTenant> builder)
        {
            builder.ToTable(
                "BuildingManagementContractTenants"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.ContractId)
                .IsRequired();

            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.Role)
                .IsRequired();

            builder.HasIndex(
                    x => new
                    {
                        x.ContractId,
                        x.TenantId
                    }
                )
                .IsUnique();

            builder.HasOne<Contract>()
                .WithMany()
                .HasForeignKey(x => x.ContractId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(x => x.TenantId);
        }
    }
}