using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Read-only/versioned room-specific source tariff. Invoice adapters must
/// keep the exact original tariff-version Id and SHA in immutable evidence.
/// Monetary and tenancy mutation cannot be authorized by this mapping.
/// </summary>
public sealed class RoomScopedServiceTariffVersionConfiguration
    : IEntityTypeConfiguration<RoomScopedServiceTariffVersion>
{
    public void Configure(EntityTypeBuilder<RoomScopedServiceTariffVersion> b)
    {
        b.ToTable("BuildingManagementRoomScopedServiceTariffVersions");
        b.HasKey(x => x.Id);
        b.Property(x => x.RoomId).IsRequired();
        b.Property(x => x.ServiceId).IsRequired();
        b.Property(x => x.Version).IsRequired();
        b.Property(x => x.UnitPriceVnd).HasPrecision(18, 0).IsRequired();
        b.Property(x => x.BillingUnit).HasConversion<int>().IsRequired();
        b.Property(x => x.ChargingRule).HasConversion<int>().IsRequired();
        b.Property(x => x.EffectiveFrom).IsRequired();
        b.Property(x => x.EffectiveThrough);
        b.Property(x => x.ApprovedByUserId).IsRequired();
        b.Property(x => x.ApprovedAtUtc).IsRequired();
        b.Property(x => x.SignedPolicyReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<Room>().WithMany()
            .HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RoomId, x.ServiceId, x.Version })
            .HasDatabaseName("UX_BM_RoomScopedServiceTariff_Version").IsUnique();
        b.HasIndex(x => new { x.RoomId, x.ServiceId, x.EffectiveFrom })
            .HasDatabaseName("UX_BM_RoomScopedServiceTariff_Start").IsUnique();
    }
}
