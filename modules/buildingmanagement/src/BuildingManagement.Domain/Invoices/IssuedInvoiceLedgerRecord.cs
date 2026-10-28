using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Immutable snapshot for the future issued-invoice ledger. There is
/// deliberately NO public factory, Issue() method or HTTP writer: rows
/// may only originate from a future audited, permissioned transaction.
/// A staged review is NOT billing authority. This entity itself does not
/// allocate payments, reduce debt, refund deposits or close a move-out.
/// </summary>
public sealed class IssuedInvoiceLedgerRecord : Entity<Guid>
{
    public Guid DraftInvoiceId { get; private set; }
    public Guid StagedEvidenceId { get; private set; }
    public Guid ReviewTicketId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public string InvoiceNumber { get; private set; } = string.Empty;
    public Guid IssuedByUserId { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal BaseAmount { get; private set; }
    public decimal AuxiliaryAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;
    public string FinancialTermsJson { get; private set; } = string.Empty;
    public string FinancialTermsSha256 { get; private set; } = string.Empty;

    private IssuedInvoiceLedgerRecord() { }

    /// <summary>
    /// Re-checks immutable indexed metadata against the financial JSON,
    /// archived physical evidence and independent design review. This is
    /// a read-side integrity check, not issuance authorization.
    /// </summary>
    public void VerifyStoredTerms(
        FinalInvoiceIssueEvidenceRecord evidence,
        InvoiceIssuanceReviewTicket review)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(review);
        evidence.VerifyStoredEvidence();
        review.VerifyStagedEvidence(evidence);

        if (Id == Guid.Empty || DraftInvoiceId != evidence.DraftInvoiceId ||
            StagedEvidenceId != evidence.Id || ReviewTicketId != review.Id ||
            MoveOutRequestId != evidence.MoveOutRequestId ||
            ContractId != evidence.ContractId || RoomId != evidence.RoomId ||
            BuildingId != review.BuildingId ||
            BillingYear != evidence.BillingYear ||
            BillingMonth != evidence.BillingMonth ||
            BaseAmount != evidence.KnownBaseChargeTotal ||
            BaseAmount <= 0m || AuxiliaryAmount < 0m ||
            TotalAmount != BaseAmount + AuxiliaryAmount ||
            IssuedByUserId == Guid.Empty ||
            IssuedAtUtc == default || IssuedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewTicketId == Guid.Empty ||
            review.Status != InvoiceIssuanceReviewStatus.ReviewedForDesign ||
            !review.ReviewedAtUtc.HasValue ||
            review.ReviewedAtUtc.Value > IssuedAtUtc ||
            DueDate == default || string.IsNullOrWhiteSpace(InvoiceNumber) ||
            InvoiceNumber.Trim().Length > 64 ||
            !StringComparer.Ordinal.Equals(EvidenceSha256, evidence.EvidenceSha256))
            throw new InvalidDataException("Issued-invoice ledger metadata is inconsistent.");

        if (string.IsNullOrWhiteSpace(FinancialTermsJson) ||
            string.IsNullOrWhiteSpace(FinancialTermsSha256) ||
            !StringComparer.Ordinal.Equals(FinancialTermsSha256,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(FinancialTermsJson))).ToLowerInvariant()))
            throw new InvalidDataException("Issued-invoice financial terms hash differs.");

        try
        {
            using var json = JsonDocument.Parse(FinancialTermsJson);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("BaseAmount").GetDecimal() != BaseAmount ||
                root.GetProperty("AuxiliaryAmount").GetDecimal() != AuxiliaryAmount ||
                root.GetProperty("TotalAmount").GetDecimal() != TotalAmount ||
                DateOnly.ParseExact(
                    root.GetProperty("DueDate").GetString()!, "yyyy-MM-dd") != DueDate ||
                root.GetProperty("DraftInvoiceId").GetGuid() != DraftInvoiceId ||
                root.GetProperty("EvidenceSha256").GetString() != EvidenceSha256)
                throw new InvalidDataException(
                    "Financial terms JSON does not match immutable ledger fields.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or
            ArgumentException or InvalidOperationException or OverflowException or
            KeyNotFoundException)
        {
            throw new InvalidDataException(
                "Financial terms archive is unreadable or incomplete.", ex);
        }
    }
}
