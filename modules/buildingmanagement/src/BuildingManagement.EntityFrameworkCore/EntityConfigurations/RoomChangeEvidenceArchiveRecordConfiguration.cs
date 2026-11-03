using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class RoomChangeEvidenceArchiveRecordConfiguration
    : IEntityTypeConfiguration<RoomChangeEvidenceArchiveRecord>
{
    public void Configure(EntityTypeBuilder<RoomChangeEvidenceArchiveRecord> b)
    {
        b.ToTable("BuildingManagementRoomChangeEvidenceArchives");
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestId).IsRequired();
        b.Property(x => x.SourceContractId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.Kind).HasConversion<int>().IsRequired();
        b.Property(x => x.EvidenceId).IsRequired();
        b.Property(x => x.SourceEvidenceSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.ParentEvidenceId).IsRequired();
        b.Property(x => x.ParentEvidenceSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.EvidenceJson).HasColumnType("text").IsRequired();
        b.Property(x => x.EvidenceJsonSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.ArchivedByUserId).IsRequired();
        b.Property(x => x.ArchivedAtUtc).IsRequired();
        b.Property(x => x.RecordSha256).HasMaxLength(64).IsRequired();
        b.Ignore(x => x.CanPostReceivable);
        b.Ignore(x => x.CanRefundDeposit);
        b.Ignore(x => x.CanExecuteRoomChange);
        b.HasOne<RoomChangeRequest>().WithMany()
            .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contract>().WithMany()
            .HasForeignKey(x => x.SourceContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Kind, x.EvidenceId })
            .HasDatabaseName("UX_BM_2HArchive_KindEvidence").IsUnique();
        b.HasIndex(x => new { x.RequestId, x.Kind, x.ParentEvidenceId })
            .HasDatabaseName("UX_BM_2HArchive_RequestParent").IsUnique();
    }
}
