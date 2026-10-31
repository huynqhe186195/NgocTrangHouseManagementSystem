using BuildingManagement.RoomChanges;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeHandoverEvidenceConfiguration
    : IEntityTypeConfiguration<RoomChangeHandoverEvidenceRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeHandoverEvidenceRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeHandoverEvidence");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.SourceRoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.HandoverDate).HasColumnType("date").IsRequired();
        b.Property(x => x.ElectricityMeterId).IsRequired();
        b.Property(x => x.ElectricityReadingId).IsRequired();
        b.Property(x => x.ElectricityFinalReading).HasPrecision(18, 3).IsRequired();
        b.Property(x => x.WaterMeterId).IsRequired();
        b.Property(x => x.WaterReadingId).IsRequired();
        b.Property(x => x.WaterFinalReading).HasPrecision(18, 3).IsRequired();
        b.Property(x => x.InspectionEvidenceReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.SignedHandoverReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.AttestedByUserId).IsRequired();
        b.Property(x => x.AttestedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UtilityMeter>().WithMany()
            .HasForeignKey(x => x.ElectricityMeterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UtilityMeter>().WithMany()
            .HasForeignKey(x => x.WaterMeterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MeterReading>().WithMany()
            .HasForeignKey(x => x.ElectricityReadingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MeterReading>().WithMany()
            .HasForeignKey(x => x.WaterReadingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId)
            .HasDatabaseName("UX_BM_RoomChangeHandover_Request").IsUnique();
    }
}
