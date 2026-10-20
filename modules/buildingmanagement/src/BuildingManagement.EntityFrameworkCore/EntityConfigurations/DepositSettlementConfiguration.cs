using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public class DepositSettlementConfiguration
    : IEntityTypeConfiguration<DepositSettlement>
{
    public void Configure(
        EntityTypeBuilder<DepositSettlement> builder)
    {
        builder.ToTable(
            "BuildingManagementDepositSettlements"
        );

        builder.ConfigureByConvention();

        builder.Property(x => x.Type)
            .IsRequired();

        builder.Property(x => x.Status)
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasPrecision(18, 0)
            .IsRequired();

        builder.Property(x => x.PaymentId)
            .IsRequired(false);

        builder.Property(
                x => x.SourceRoomReservationId)
            .IsRequired(false);

        builder.Property(x => x.SourceContractId)
            .IsRequired(false);

        builder.Property(x => x.TargetContractId)
            .IsRequired(false);

        builder.Property(x => x.CompletedAt)
            .HasColumnType(
                "timestamp without time zone"
            )
            .IsRequired(false);

        builder.Property(x => x.Notes)
            .HasMaxLength(500)
            .IsRequired(false);

        // Query indexes
        builder.HasIndex(x => x.PaymentId);

        builder.HasIndex(
            x => x.SourceRoomReservationId
        );

        builder.HasIndex(x => x.SourceContractId);

        builder.HasIndex(x => x.TargetContractId);

        builder.HasIndex(x => new
        {
            x.Type,
            x.Status
        });

        // Foreign keys
        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(x => x.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RoomReservation>()
            .WithMany()
            .HasForeignKey(
                x => x.SourceRoomReservationId
            )
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(x => x.SourceContractId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(x => x.TargetContractId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}