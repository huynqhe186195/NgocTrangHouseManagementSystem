using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// STAGED review model (SQLite tests only). No PostgreSQL migration, online
/// submit/review service or audited invoice-posting transaction exists.
/// Stronger append-only DB role/trigger enforcement is required before use.
/// </summary>
public sealed class InvoiceIssuanceReviewTicketConfiguration
    : IEntityTypeConfiguration<InvoiceIssuanceReviewTicket>
{
    public void Configure(EntityTypeBuilder<InvoiceIssuanceReviewTicket> builder)
    {
        builder.ToTable("BuildingManagementInvoiceIssuanceReviews", t =>
        {
            t.HasCheckConstraint(
                "CK_BM_IssueReview_Status",
                "\"Status\" IN (1, 2, 3)");
            t.HasCheckConstraint(
                "CK_BM_IssueReview_Reason",
                "(\"Status\" = 1 AND \"ReviewReason\" IS NULL AND " +
                "\"ReviewedByUserId\" IS NULL AND \"ReviewedAtUtc\" IS NULL) OR " +
                "(\"Status\" IN (2, 3) AND \"ReviewedByUserId\" IS NOT NULL " +
                "AND \"ReviewedAtUtc\" IS NOT NULL AND \"ReviewReason\" IS NOT NULL " +
                "AND length(trim(\"ReviewReason\")) BETWEEN 8 AND 500)");
            t.HasCheckConstraint(
                "CK_BM_IssueReview_IndependentReviewer",
                "\"ReviewedByUserId\" IS NULL OR " +
                "\"ReviewedByUserId\" <> \"SubmittedByUserId\"");
            t.HasCheckConstraint(
                "CK_BM_IssueReview_Hash",
                "length(\"EvidenceSha256\") = 64 AND " +
                "\"EvidenceSha256\" = lower(\"EvidenceSha256\")");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StagedEvidenceId).IsRequired();
        builder.Property(x => x.DraftInvoiceId).IsRequired();
        builder.Property(x => x.MoveOutRequestId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SubmittedByUserId).IsRequired();
        builder.Property(x => x.SubmittedAtUtc).IsRequired();
        builder.Property(x => x.SubmissionNote).HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.ReviewedByUserId).IsRequired(false);
        builder.Property(x => x.ReviewedAtUtc).IsRequired(false);
        builder.Property(x => x.ReviewReason).HasMaxLength(500).IsRequired(false);

        builder.HasIndex(x => x.StagedEvidenceId).IsUnique()
            .HasDatabaseName("UX_BM_IssueReview_Evidence");
        builder.HasIndex(x => x.DraftInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_IssueReview_Draft");
        builder.HasIndex(x => new
        {
            x.ContractId, x.DraftInvoiceId
        }).HasDatabaseName("IX_BM_IssueReview_Contract_Draft");

        builder.HasOne<FinalInvoiceIssueEvidenceRecord>().WithMany()
            .HasForeignKey(x => x.StagedEvidenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
