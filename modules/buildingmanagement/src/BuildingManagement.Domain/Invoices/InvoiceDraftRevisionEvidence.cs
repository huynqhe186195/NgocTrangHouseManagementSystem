using System;
using System.Linq;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// NON-PERSISTED design prototype for a future append-only draft revision
/// audit ledger. Holds an independent, immutable copy of the *previous*
/// utility-only draft, never an additional payable invoice.
///
/// This type does NOT add a database table, archive anything automatically,
/// authorize a caller, rebuild a draft or release a source uniqueness key.
/// </summary>
public sealed class InvoiceDraftRevisionEvidence
{
    public Guid InvoiceId { get; }
    public int ArchivedRevision { get; }
    public Guid ArchivedByUserId { get; }
    public DateTime ArchivedAtUtc { get; }
    public string Reason { get; }
    public InvoiceDraftSnapshot PreviousDraft { get; }

    private InvoiceDraftRevisionEvidence(
        Guid invoiceId, int archivedRevision,
        Guid archivedByUserId, DateTime archivedAtUtc,
        string reason, InvoiceDraftSnapshot previousDraft)
    {
        InvoiceId = invoiceId;
        ArchivedRevision = archivedRevision;
        ArchivedByUserId = archivedByUserId;
        ArchivedAtUtc = archivedAtUtc;
        Reason = reason;
        PreviousDraft = previousDraft;
    }

    public static InvoiceDraftRevisionEvidence Capture(
        Guid invoiceId,
        int archivedRevision,
        Guid archivedByUserId,
        DateTime archivedAtUtc,
        string reason,
        InvoiceDraftSnapshot previousDraft)
    {
        if (invoiceId == Guid.Empty)
            throw new ArgumentException("Invoice ID is required.", nameof(invoiceId));
        if (archivedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(archivedRevision));
        if (archivedByUserId == Guid.Empty)
            throw new ArgumentException("An identified operator is required.",
                nameof(archivedByUserId));
        if (archivedAtUtc.Kind != DateTimeKind.Utc ||
            archivedAtUtc == default)
            throw new ArgumentException("Archive timestamp must be UTC.",
                nameof(archivedAtUtc));

        var normalizedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason) ||
            normalizedReason.Length < 8 || normalizedReason.Length > 500)
            throw new ArgumentException("Correction reason must have 8–500 characters.",
                nameof(reason));

        ArgumentNullException.ThrowIfNull(previousDraft);
        if (previousDraft.UtilityLines.Count != 2 ||
            previousDraft.UtilityLines.Select(x => x.UtilityType)
                .ToHashSet().SetEquals(new[]
                {
                    UtilityType.Electricity,
                    UtilityType.Water
                }) == false ||
            previousDraft.ProvisionalTotal is null ||
            previousDraft.UtilityLines.Any(x =>
                x.ProvisionalAmount is null ||
                x.MeterSegments.Count == 0 ||
                x.CoverageIssues.Count != 0))
            throw new ArgumentException(
                "Only a complete prior utility draft can be revision evidence.",
                nameof(previousDraft));

        // Re-validate and copy the entire previous snapshot by VALUE,
        // including tariff and all physical meter source counter ranges.
        // Never keep a caller-owned list as historical evidence.
        var frozen = InvoiceDraftSnapshot.Capture(
            previousDraft.ContractId,
            previousDraft.RoomId,
            previousDraft.BillingYear,
            previousDraft.BillingMonth,
            previousDraft.UtilityLines);

        if (frozen.ProvisionalTotal != previousDraft.ProvisionalTotal)
            throw new ArgumentException("Archived total is inconsistent.",
                nameof(previousDraft));

        return new InvoiceDraftRevisionEvidence(
            invoiceId, archivedRevision, archivedByUserId,
            archivedAtUtc, normalizedReason, frozen);
    }
}
