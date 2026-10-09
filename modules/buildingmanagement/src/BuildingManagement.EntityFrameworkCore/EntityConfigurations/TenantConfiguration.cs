using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class TenantConfiguration
        : IEntityTypeConfiguration<Tenant>
    {
        public void Configure(
            EntityTypeBuilder<Tenant> builder)
        {
            builder.ToTable(
                "BuildingManagementTenants"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.FullName)
                .IsRequired()
                .HasMaxLength(128);

            builder.Property(x => x.PhoneNumber)
                .HasMaxLength(20);

            builder.Property(x => x.Email)
                .HasMaxLength(256);

            builder.Property(x => x.DateOfBirth)
                .HasColumnType("date");

            builder.Property(x => x.IdentityNumber)
                .HasMaxLength(50);

            builder.Property(x => x.PermanentAddress)
                .HasMaxLength(256);

            builder.Property(x => x.EmergencyContactName)
                .HasMaxLength(128);

            builder.Property(x => x.EmergencyContactPhone)
                .HasMaxLength(20);

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(
                    x => new
                    {
                        x.IdentityType,
                        x.IdentityNumber
                    }
                )
                .IsUnique();
        }
    }
}
