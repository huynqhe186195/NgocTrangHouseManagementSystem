using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class ContractConfiguration
        : IEntityTypeConfiguration<Contract>
    {
        public void Configure(
            EntityTypeBuilder<Contract> builder)
        {
            builder.ToTable(
                "BuildingManagementContracts"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.ContractNumber)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.StartDate)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(x => x.EndDate)
                .HasColumnType("date");

            builder.Property(x => x.MonthlyRent)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.DepositAmount)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.Property(x => x.RenewedFromContractId)
    .IsRequired(false);

            builder.HasIndex(x => x.RenewedFromContractId)
    .IsUnique();

            builder.HasOne<Contract>()
    .WithMany()
    .HasForeignKey(x => x.RenewedFromContractId)
    .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(x => x.ContractNumber)
                .IsUnique();

            builder.HasIndex(x => x.RoomId);

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}