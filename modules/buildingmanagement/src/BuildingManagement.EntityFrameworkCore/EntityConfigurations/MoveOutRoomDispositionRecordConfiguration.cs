using BuildingManagement.MoveOuts;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

public sealed class MoveOutRoomDispositionRecordConfiguration
    : IEntityTypeConfiguration<MoveOutRoomDispositionRecord>
{
    public void Configure(EntityTypeBuilder<MoveOutRoomDispositionRecord> b)
    {
        b.ToTable("BuildingManagementMoveOutRoomDispositions");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContractId).IsRequired();
        b.Property(x => x.MoveOutRequestId).IsRequired();
        b.Property(x => x.RoomId).IsRequired();
        b.Property(x => x.BuildingId).IsRequired();
        b.Property(x => x.CloseoutApprovalId).IsRequired();
        b.Property(x => x.CompletedByUserId).IsRequired();
        b.Property(x => x.CompletedAtUtc).IsRequired();
        b.Property(x => x.ResultRoomStatus).IsRequired();
        b.Property(x => x.FutureReservationId).IsRequired(false);
        b.Property(x => x.FutureSignedContractId).IsRequired(false);
        b.Property(x => x.RequiresMaintenance).IsRequired();
        b.Property(x => x.MaintenanceReason).HasMaxLength(500).IsRequired(false);
        b.Property(x => x.MaintenanceEvidenceReference).HasMaxLength(500).IsRequired(false);
        b.Property(x => x.HandoverEvidenceReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.DispositionJson).HasColumnType("text").IsRequired();
        b.Property(x => x.DispositionSha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ContractId).IsUnique()
            .HasDatabaseName("UX_BM_MoveOutDisposition_Contract");
        b.HasIndex(x => x.MoveOutRequestId).IsUnique()
            .HasDatabaseName("UX_BM_MoveOutDisposition_Request");
        b.HasIndex(x => x.CloseoutApprovalId).IsUnique()
            .HasDatabaseName("UX_BM_MoveOutDisposition_Approval");
    }
}
