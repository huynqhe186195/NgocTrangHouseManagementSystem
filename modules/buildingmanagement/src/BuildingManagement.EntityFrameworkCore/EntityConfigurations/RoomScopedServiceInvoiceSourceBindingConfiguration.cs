using BuildingManagement.Invoices;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Original, frozen monthly service-source evidence, linked to the exact
/// candidate and immutable room-specific tariff version. This mapping does
/// not issue a bill or authorize a financial mutation.
/// </summary>
public sealed class RoomScopedServiceInvoiceSourceBindingConfiguration
    : IEntityTypeConfiguration<RoomScopedServiceInvoiceSourceBinding>
{
    public void Configure(EntityTypeBuilder<RoomScopedServiceInvoiceSourceBinding> b)
    {
        b.ToTable("BuildingManagementRoomServiceInvoiceSourceBindings");
        b.HasKey(x => new { x.MonthlyCandidateId, x.ServiceId });
        b.Property(x => x.MonthlyCandidateId).IsRequired();
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.RoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.BillingYear).IsRequired();
        b.Property(x => x.BillingMonth).IsRequired();
        b.Property(x => x.ServiceId).IsRequired();
        b.Property(x => x.ServiceCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.OriginalTariffVersionId).IsRequired();
        b.Property(x => x.OriginalTariffVersion).IsRequired();
        b.Property(x => x.OriginalTariffSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.OriginalFullPeriodUnitPriceVnd)
            .HasPrecision(18, 0).IsRequired();
        b.Property(x => x.BillingUnit).HasConversion<int>().IsRequired();
        b.Property(x => x.ChargingRule).HasConversion<int>().IsRequired();
        b.Property(x => x.ServiceFromVietnam).HasColumnType("date").IsRequired();
        b.Property(x => x.ServiceThroughVietnam).HasColumnType("date").IsRequired();
        b.Property(x => x.VerifiedOccupantCount).IsRequired();
        b.Property(x => x.VerifiedMeterQuantity).HasColumnType("numeric");
        b.Property(x => x.VerifiedChargeSourceReference)
            .HasMaxLength(500).IsRequired();
        b.Property(x => x.FrozenAuxiliaryLineAmountVnd)
            .HasPrecision(18, 0).IsRequired();
        b.Property(x => x.OriginalMonthlyCandidateSha256)
            .HasMaxLength(64).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();

        b.HasOne<MonthlyInvoiceCandidateLedgerRecord>().WithMany()
            .HasForeignKey(x => x.MonthlyCandidateId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomScopedServiceTariffVersion>().WithMany()
            .HasForeignKey(x => x.OriginalTariffVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.MonthlyCandidateId, x.ServiceCode })
            .HasDatabaseName("IX_BM_RoomServiceInvoice_SourceCode");
        b.HasIndex(x => x.OriginalTariffVersionId)
            .HasDatabaseName("IX_BM_RoomServiceInvoice_TariffVersion");
    }
}
