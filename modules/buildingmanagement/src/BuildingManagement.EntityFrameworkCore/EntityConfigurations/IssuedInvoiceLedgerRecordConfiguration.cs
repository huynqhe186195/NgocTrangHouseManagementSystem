using BuildingManagement.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// Schema/mapping only. No public writer, issuance factory or API endpoint.
/// PostgreSQL trigger protections live in the source-only migration.
/// </summary>
public sealed class IssuedInvoiceLedgerRecordConfiguration
    : IEntityTypeConfiguration<IssuedInvoiceLedgerRecord>
{
    public void Configure(EntityTypeBuilder<IssuedInvoiceLedgerRecord> builder)
    {
        builder.ToTable("BuildingManagementIssuedInvoiceLedger", t =>
        {
            t.HasCheckConstraint("CK_BM_IssuedLedger_Amounts",
                "\"BaseAmount\" > 0 AND \"AuxiliaryAmount\" >= 0 AND " +
                "\"TotalAmount\" = \"BaseAmount\" + \"AuxiliaryAmount\"");
            t.HasCheckConstraint("CK_BM_IssuedLedger_Period",
                "\"BillingYear\" BETWEEN 2000 AND 2200 AND " +
                "\"BillingMonth\" BETWEEN 1 AND 12");
            t.HasCheckConstraint("CK_BM_IssuedLedger_Number",
                "length(trim(\"InvoiceNumber\")) BETWEEN 1 AND 64");
            t.HasCheckConstraint("CK_BM_IssuedLedger_TermsJson",
                "length(\"FinancialTermsJson\") BETWEEN 1 AND 1000000");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DraftInvoiceId).IsRequired();
        builder.Property(x => x.StagedEvidenceId).IsRequired();
        builder.Property(x => x.ReviewTicketId).IsRequired();
        builder.Property(x => x.MoveOutRequestId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.RoomId).IsRequired();
        builder.Property(x => x.BuildingId).IsRequired();
        builder.Property(x => x.BillingYear).IsRequired();
        builder.Property(x => x.BillingMonth).IsRequired();
        builder.Property(x => x.InvoiceNumber).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IssuedByUserId).IsRequired();
        builder.Property(x => x.IssuedAtUtc).IsRequired();
        builder.Property(x => x.DueDate).HasColumnType("date").IsRequired();
        builder.Property(x => x.BaseAmount).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.AuxiliaryAmount).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.TotalAmount).HasPrecision(18, 3).IsRequired();
        builder.Property(x => x.EvidenceSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FinancialTermsJson).HasColumnType("text").IsRequired();
        builder.Property(x => x.FinancialTermsSha256).HasMaxLength(64).IsRequired();

        builder.HasIndex(x => x.InvoiceNumber).IsUnique()
            .HasDatabaseName("UX_BM_IssuedLedger_Number");
        builder.HasIndex(x => x.DraftInvoiceId).IsUnique()
            .HasDatabaseName("UX_BM_IssuedLedger_Draft");
        builder.HasIndex(x => x.StagedEvidenceId).IsUnique()
            .HasDatabaseName("UX_BM_IssuedLedger_Evidence");
        builder.HasIndex(x => x.ReviewTicketId).IsUnique()
            .HasDatabaseName("UX_BM_IssuedLedger_Review");
        builder.HasIndex(x => new
        {
            x.ContractId, x.BillingYear, x.BillingMonth
        }).IsUnique().HasDatabaseName("UX_BM_IssuedLedger_Contract_Period");
        builder.HasIndex(x => new
        {
            x.RoomId, x.BillingYear, x.BillingMonth
        }).IsUnique().HasDatabaseName("UX_BM_IssuedLedger_Room_Period");

        builder.HasOne<Invoice>().WithMany()
            .HasForeignKey(x => x.DraftInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinalInvoiceIssueEvidenceRecord>().WithMany()
            .HasForeignKey(x => x.StagedEvidenceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InvoiceIssuanceReviewTicket>().WithMany()
            .HasForeignKey(x => x.ReviewTicketId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
