using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BuildingManagement.EntityFrameworkCore.EntityConfigurations;

/// <summary>
/// STAGED allocation journal for the isolated EF test model only.
/// Invoice is intentionally NOT FK-linked: the current Invoice table
/// contains Draft records, NEVER issued invoices. A reviewed PostgreSQL
/// migration must introduce a real issued-invoice FK, append-only rules,
/// idempotent writes, authorization and transactional allocation checks.
/// </summary>
public sealed class InvoicePaymentAllocationJournalEntryConfiguration
    : IEntityTypeConfiguration<InvoicePaymentAllocationJournalEntry>
{
    public void Configure(
        EntityTypeBuilder<InvoicePaymentAllocationJournalEntry> builder)
    {
        builder.ToTable("BuildingManagementInvoicePaymentAllocationJournal", t =>
        {
            t.HasCheckConstraint(
                "CK_BM_InvoiceAllocation_Positive",
                "\"Amount\" > 0 AND \"Amount\" = CAST(\"Amount\" AS bigint)");
            t.HasCheckConstraint(
                "CK_BM_InvoiceAllocation_PaymentNumber",
                "length(trim(\"PaymentNumberSnapshot\")) BETWEEN 1 AND 64");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.PaymentId).IsRequired();
        builder.Property(x => x.IssuedInvoiceId).IsRequired();
        builder.Property(x => x.ContractId).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 0).IsRequired();
        builder.Property(x => x.PaymentNumberSnapshot)
            .HasMaxLength(64).IsRequired();
        builder.Property(x => x.PaymentPaidAtUtc).IsRequired();
        builder.Property(x => x.PaymentTransactionReferenceSnapshot)
            .HasMaxLength(128).IsRequired(false);
        builder.Property(x => x.RecordedByUserId).IsRequired();
        builder.Property(x => x.RecordedAtUtc).IsRequired();

        // Strict initial model: one receipt can be assigned to one invoice
        // at most once. Cross-invoice splitting requires new approved policy.
        builder.HasIndex(x => x.PaymentId).IsUnique()
            .HasDatabaseName("UX_BM_InvoiceAllocation_Payment");
        builder.HasIndex(x => new { x.IssuedInvoiceId, x.PaymentId })
            .IsUnique()
            .HasDatabaseName("UX_BM_InvoiceAllocation_Invoice_Payment");
        builder.HasIndex(x => x.ContractId)
            .HasDatabaseName("IX_BM_InvoiceAllocation_Contract");

        builder.HasOne<Payment>().WithMany()
            .HasForeignKey(x => x.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
