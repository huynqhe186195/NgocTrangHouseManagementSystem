using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityConfigurations
{
    public class RoomConfiguration : IEntityTypeConfiguration<Room>
    {
        public void Configure(EntityTypeBuilder<Room> builder)
        {
            builder.ToTable(
                BuildingManagementDbProperties.DbTablePrefix + "Rooms",
                BuildingManagementDbProperties.DbSchema
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.FloorId)
                .IsRequired();

            builder.Property(x => x.RoomNumber)
                .IsRequired()
                .HasMaxLength(32);

            builder.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(128);

            builder.Property(x => x.Area)
                .HasPrecision(10, 2);

            builder.Property(x => x.Capacity)
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.Description)
                .HasMaxLength(500);

            builder.HasIndex(x => new
            {
                x.FloorId,
                x.RoomNumber
            })
            .IsUnique();

            builder.HasOne<Floor>() // Relationship of floor and room, if want to create room obligatory must have floor
                .WithMany()
                .HasForeignKey(x => x.FloorId)
                .OnDelete(DeleteBehavior.Restrict); // If floor having room cannot delete any thing
        }
    }
}
