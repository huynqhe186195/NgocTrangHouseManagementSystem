using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// STAGED model: SQLite tests only until a reviewed PostgreSQL migration,
/// audited posting transaction, write privileges and UPDATE/DELETE
/// protection exist. A unique preparation row is NOT proof of issuance.
/// </summary>
public sealed class FinalInvoiceIssueEvidenceRecordConfiguration
    : IEntityTypeConfiguration<FinalInvoiceIssueEvidenceRecord>
{
    public void Configure(EntityTypeBuilder<FinalInvoiceIssueEvidenceRecord> builder)
    {
        builder.ToTable("BuildingManagementFinalInvoiceIssueEvidence", t =>
        {
            t.HasCheckConstraint(
                "CK_BM_FinalIssueEvidence_Positive",
                "\"\"KnownBaseChargeTotal\"\" > 0".Replace("\"\"", "\""));
            t.HasCheckConstraint(
                "CK_BM_FinalIssueEvidence_Period",
                "\"BillingYear\" BETWEEN 2000 AND 2200 AND " +
                "\"BillingMonth\" BETWEEN 1 AND 12");
            t.HasCheckConstraint(
                "CK_BM_FinalIssueEvidence_Json",
                "length(\"EvidenceJson\") BETWEEN 1 AND 1000000");
            t.HasCheckConstraint(
                "CK_BM_FinalIssueEvidence_Hash",
                "length(\"EvidenceSha256\") = 64 AND " +
                "\"EvidenceSha256\" = lower(\"EvidenceSha256\")");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.DraftInvoiceId).IsRequired();
        builder.Property(x => x.MoveOutRequestId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BillingYear).IsRequired();
        builder.Property(x => x.BillingMonth).IsRequired();
        builder.Property(x => x.PreparedByUserId).IsRequired();
        builder.Property(x => x.PreparedAtUtc).IsRequired();
        builder.Property(x => x.KnownBaseChargeTotal)
            .HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.EvidenceJson)
            .HasColumnType("text").IsRequired();
        builder.Property(x => x.EvidenceSha256)
            .HasMaxLength(64).IsRequired();

        builder.HasIndex(x => x.DraftInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_FinalIssueEvidence_Draft");
        builder.HasIndex(x => new
        {
            x.ContractId, x.BillingYear, x.BillingMonth
        }).IsUnique()
            .HasDatabaseName("UX_BM_FinalIssueEvidence_Contract_Period");
        builder.HasIndex(x => new
        {
            x.RoomId, x.BillingYear, x.BillingMonth
        }).IsUnique()
            .HasDatabaseName("UX_BM_FinalIssueEvidence_Room_Period");

        builder.HasOne<Invoice>().WithMany()
            .HasForeignKey(x => x.DraftInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MoveOutRequest>().WithMany()
            .HasForeignKey(x => x.MoveOutRequestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
