using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class ContractRenewalHoldConfiguration
        : IEntityTypeConfiguration<ContractRenewalHold>
    {
        public void Configure(
            EntityTypeBuilder<ContractRenewalHold> builder)
        {
            builder.ToTable(
                "BuildingManagementContractRenewalHolds"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.CurrentContractId)
                .IsRequired();

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.RequestedAt)
                .HasColumnType("timestamp without time zone")
                .IsRequired();

            builder.Property(x => x.ExpiresAt)
                .HasColumnType("timestamp without time zone")
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.CompletedContractId)
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(x => x.CurrentContractId)
                .IsUnique();

            builder.HasIndex(x => x.CompletedContractId)
                .IsUnique();

            builder.HasIndex(x => new
            {
                x.RoomId,
                x.Status,
                x.ExpiresAt
            });

            builder.HasOne<Contract>()
                .WithMany()
                .HasForeignKey(x => x.CurrentContractId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Contract>()
                .WithMany()
                .HasForeignKey(x => x.CompletedContractId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}