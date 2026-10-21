using BuildingManagement.Contracts;
using BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations
{
    public class PaymentConfiguration
        : IEntityTypeConfiguration<Payment>
    {
        public void Configure(
            EntityTypeBuilder<Payment> builder)
        {
            builder.ToTable(
                "BuildingManagementPayments"
            );

            builder.ConfigureByConvention();

            builder.Property(x => x.PaymentNumber)
                .IsRequired()
                .HasMaxLength(64);

            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.Purpose)
                .IsRequired();

            builder.Property(x => x.Method)
                .IsRequired();

            builder.Property(x => x.Channel)
                .IsRequired();

            builder.Property(x => x.Status)
                .IsRequired();

            builder.Property(x => x.Amount)
                .HasPrecision(18, 0)
                .IsRequired();

            builder.Property(x => x.PaidAt)
                .HasColumnType(
                    "timestamp with time zone"
                )
                .IsRequired(false);

            builder.Property(x => x.RoomReservationId)
                .IsRequired(false);

            builder.Property(x => x.ContractId)
                .IsRequired(false);

            builder.Property(x => x.InvoiceId)
                .IsRequired(false);

            builder.Property(x => x.DepositDisposition)
                .IsRequired(false);

            builder.Property(x => x.TransactionReference)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(500)
                .IsRequired(false);

            // Unique business identifiers
            builder.HasIndex(x => x.PaymentNumber)
                .IsUnique();

            builder.HasIndex(x => x.TransactionReference)
                .IsUnique();

            // Query indexes
            builder.HasIndex(x => x.TenantId);

            builder.HasIndex(x => x.RoomReservationId);

            builder.HasIndex(x => x.ContractId);

            builder.HasIndex(x => x.InvoiceId);

            builder.HasIndex(x => new
            {
                x.Purpose,
                x.Status,
                x.PaidAt
            });

            // Foreign keys
            builder.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<RoomReservation>()
                .WithMany()
                .HasForeignKey(
                    x => x.RoomReservationId
                )
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne<Contract>()
                .WithMany()
                .HasForeignKey(x => x.ContractId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}