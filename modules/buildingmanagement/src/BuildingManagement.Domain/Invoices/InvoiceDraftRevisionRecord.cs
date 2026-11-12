using System;
using System.IO;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Staged EF persistence model for a PREVIOUS draft's historical evidence.
/// This is not an invoice, debt, or active meter-source attribution.
/// There is deliberately no public mutation, Delete, Issue or Post method.
///
/// WARNING: an approved PostgreSQL migration/immutability trigger and
/// authorized transactional rebuild do NOT exist yet. This class alone
/// does not enforce append-only persistence in the database.
/// </summary>
public sealed class InvoiceDraftRevisionRecord : Entity<Guid>
{
    public Guid InvoiceId { get; private set; }
    public int ArchivedRevision { get; private set; }
    public Guid ArchivedByUserId { get; private set; }
    public DateTime ArchivedAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string EvidenceJson { get; private set; } = string.Empty;
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private InvoiceDraftRevisionRecord() { }

    private InvoiceDraftRevisionRecord(
        Guid id, InvoiceDraftRevisionEvidence evidence,
        InvoiceDraftRevisionArchivePackage package) : base(id)
    {
        InvoiceId = evidence.InvoiceId;
        ArchivedRevision = evidence.ArchivedRevision;
        ArchivedByUserId = evidence.ArchivedByUserId;
        ArchivedAtUtc = evidence.ArchivedAtUtc;
        Reason = evidence.Reason;
        EvidenceJson = package.EvidenceJson;
        EvidenceSha256 = package.EvidenceSha256;
    }

    public static InvoiceDraftRevisionRecord Prepare(
        Guid id, InvoiceDraftRevisionEvidence evidence)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Revision record ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(evidence);
        // Evidence has already been validated by Capture. Serialize
        // precisely once; the active invoice must not be mutated here.
        var package = InvoiceDraftRevisionArchiveCodec.Pack(evidence);
        return new InvoiceDraftRevisionRecord(id, evidence, package);
    }

    public InvoiceDraftRevisionEvidence VerifyAndRead()
    {
        InvoiceDraftRevisionEvidence decoded;
        try
        {
            decoded = InvoiceDraftRevisionArchiveCodec.Unpack(
                new InvoiceDraftRevisionArchivePackage(
                    EvidenceJson, EvidenceSha256));
        }
        catch (InvalidDataException)
        {
            throw;
        }

        if (decoded.InvoiceId != InvoiceId ||
            decoded.ArchivedRevision != ArchivedRevision ||
            decoded.ArchivedByUserId != ArchivedByUserId ||
            decoded.ArchivedAtUtc != ArchivedAtUtc ||
            !string.Equals(decoded.Reason, Reason, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Revision metadata disagrees with the archived evidence.");

        return decoded;
    }
}
