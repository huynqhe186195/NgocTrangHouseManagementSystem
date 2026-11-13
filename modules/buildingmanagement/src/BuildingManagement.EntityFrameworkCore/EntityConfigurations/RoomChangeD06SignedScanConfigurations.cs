using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeSignedHandoverScanConfiguration
    : IEntityTypeConfiguration<RoomChangeSignedHandoverScanRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeSignedHandoverScanRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeSignedHandoverScans");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.HandoverArchiveId).IsRequired();
        b.Property(x => x.HandoverEvidenceSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.HandoverDocumentSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.MediaType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ByteLength).IsRequired();
        b.Property(x => x.VaultReference).HasMaxLength(32).IsRequired();
        b.Property(x => x.UploadedByUserId).IsRequired();
        b.Property(x => x.UploadedAtUtc).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.Ignore(x => x.ProvesSignerIdentity);
        b.Ignore(x => x.CanExecuteRoomChange);
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RoomChangeEvidenceArchiveRecord>().WithMany()
            .HasForeignKey(x => x.HandoverArchiveId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RoomChangeContractDocumentAccessConfiguration
    : IEntityTypeConfiguration<RoomChangeContractDocumentAccessRecord>
{
    public void Configure(
        EntityTypeBuilder<RoomChangeContractDocumentAccessRecord> b)
    {
        b.ToTable("BuildingManagementContractDocumentAccess");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.AccessedByUserId).IsRequired();
        b.Property(x => x.AccessedAtUtc).IsRequired();
        b.Property(x => x.Purpose).HasMaxLength(300).IsRequired();
        b.Property(x => x.DeliveredDocumentSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.DeliveredByteLength).IsRequired();
        b.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        b.Ignore(x => x.GrantsAccess);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}
