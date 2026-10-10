using BuildingManagement.Contracts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class RoomReservationConfiguration
        : IEntityTypeConfiguration<RoomReservation>
    {
        public void Configure(
            EntityTypeBuilder<RoomReservation> builder)
        {
            builder.ToTable(
                "BuildingManagementRoomReservations"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.ReservationNumber)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.RoomId)
                .IsRequired();

            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.ExpectedMoveInDate)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(x => x.QuotedMonthlyRent)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.RequiredDepositAmount)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.ReservedAt)
                .HasColumnType("timestamp without time zone")
                .IsRequired(false);

            builder.Property(x => x.DepositDueDate)
                .HasColumnType("date")
                .IsRequired(false);

            builder.Property(x => x.ConvertedContractId)
                .IsRequired(false);

            builder.Property(x => x.CancelledAt)
                .HasColumnType("timestamp without time zone")
                .IsRequired(false);

            builder.Property(x => x.CancellationReason)
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(500);

            builder.HasIndex(x => x.ReservationNumber)
                .IsUnique();

            builder.HasIndex(x => new
            {
                x.RoomId,
                x.Status,
                x.ExpectedMoveInDate
            });

            builder.Property(
                    x => x.MinimumReservationDepositAmount
                )
                .HasPrecision(18, 0)
                .IsRequired()
                .HasDefaultValue(500_000m);

            builder.HasIndex(x => x.TenantId);

            builder.Property(x => x.CancellationNotes)
                    .HasMaxLength(500);

            builder.HasIndex(x => x.ConvertedContractId)
                .IsUnique();

            builder.HasOne<Room>()
                .WithMany()
                .HasForeignKey(x => x.RoomId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Contract>()
                .WithMany()
                .HasForeignKey(x => x.ConvertedContractId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}