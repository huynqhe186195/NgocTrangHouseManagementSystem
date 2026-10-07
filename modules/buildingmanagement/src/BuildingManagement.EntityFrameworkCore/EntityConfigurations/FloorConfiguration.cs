using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class FloorConfiguration : IEntityTypeConfiguration<Floor>
    {
        public void Configure(EntityTypeBuilder<Floor> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "Floors",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.BuildingId)
                .IsRequired();

            builder.Property(x => x.FloorNumber)
                .IsRequired();

            builder.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(128);

            builder.Property(x => x.Description)
                .HasMaxLength(500);

            // Within the same building, there cannot be two instances where FloorNumber = 1
            builder.HasIndex(x => new
            {
                x.BuildingId,
                x.FloorNumber
            })
            .IsUnique();

            // This code use notify system that not create floor if building not found
            builder.HasOne<Building>()
                .WithMany()
                .HasForeignKey(x => x.BuildingId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
