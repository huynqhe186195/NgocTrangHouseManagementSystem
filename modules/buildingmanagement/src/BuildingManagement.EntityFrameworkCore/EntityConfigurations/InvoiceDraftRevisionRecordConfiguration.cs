using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// STAGED model only. No EF migration is added by this configuration.
/// Offline migration review + scratch PostgreSQL DDL tests are required
/// before the ledger can be used outside the SQLite test database.
/// </summary>
public sealed class InvoiceDraftRevisionRecordConfiguration
    : IEntityTypeConfiguration<InvoiceDraftRevisionRecord>
{
    public void Configure(EntityTypeBuilder<InvoiceDraftRevisionRecord> builder)
    {
        builder.ToTable("BuildingManagementInvoiceDraftRevisions", table =>
        {
            // All expressions work in PostgreSQL and SQLite. The
            // final PostgreSQL migration must add a regex check of the
            // hex digest, and a tested UPDATE/DELETE protection trigger.
            table.HasCheckConstraint(
                "CK_BM_InvoiceDraftRevision_Positive",
                "\"ArchivedRevision\" >= 1");
            table.HasCheckConstraint(
                "CK_BM_InvoiceDraftRevision_Reason",
                "length(trim(\"Reason\")) BETWEEN 8 AND 500");
            table.HasCheckConstraint(
                "CK_BM_InvoiceDraftRevision_Evidence",
                "length(\"EvidenceJson\") BETWEEN 1 AND 1000000");
            table.HasCheckConstraint(
                "CK_BM_InvoiceDraftRevision_Hash",
                "length(\"EvidenceSha256\") = 64 AND " +
                "\"EvidenceSha256\" = lower(\"EvidenceSha256\")");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.InvoiceId).IsRequired();
        builder.Property(x => x.ArchivedRevision).IsRequired();
        builder.Property(x => x.ArchivedByUserId).IsRequired();
        builder.Property(x => x.ArchivedAtUtc).IsRequired();
        builder.Property(x => x.Reason)
            .HasMaxLength(500).IsRequired();
        builder.Property(x => x.EvidenceJson)
            .HasColumnType("text").IsRequired();
        builder.Property(x => x.EvidenceSha256)
            .HasMaxLength(64).IsRequired();

        builder.HasIndex(x => new { x.InvoiceId, x.ArchivedRevision })
            .IsUnique()
            .HasDatabaseName("UX_BM_InvoiceDraftRevision_Invoice_Number");

        builder.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
