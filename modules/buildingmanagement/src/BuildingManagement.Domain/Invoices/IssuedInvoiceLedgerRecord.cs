using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private IssuedInvoiceLedgerRecord(Guid id) : base(id) { }

    /// <summary>
    /// Builds a locally VERIFIED candidate from server-supplied facts.
    /// NOT a public Issue action and NOT posting authority: a future
    /// authenticated application writer MUST obtain fresh Identity and
    /// building-grant leases, original source locks and a financial
    /// transaction before INSERT. No runtime endpoint invokes this.
    /// </summary>
    public static IssuedInvoiceLedgerRecord BuildVerifiedCandidate(
        Guid id, string invoiceNumber, Guid issuerUserId, DateTime issuedAtUtc,
        FinalInvoiceIssueEvidenceRecord evidence,
        InvoiceIssuanceReviewTicket review,
        FinalInvoiceIssuePreflight preflight,
        InvoiceIssuanceScopeFacts scopeFacts,
        FinalInvoiceFinancialTermsProposal approvedTerms,
        FinalInvoiceIssueEvidenceDriftReview driftReview,
        FinalInvoiceSourceProvenanceReview provenance)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(scopeFacts);
        ArgumentNullException.ThrowIfNull(approvedTerms);
        ArgumentNullException.ThrowIfNull(driftReview);
        ArgumentNullException.ThrowIfNull(provenance);
        if (id == Guid.Empty || id == evidence.DraftInvoiceId ||
            issuerUserId == Guid.Empty || string.IsNullOrWhiteSpace(invoiceNumber) ||
            invoiceNumber.Trim().Length > 64 ||
            issuedAtUtc == default || issuedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A valid issued candidate identity and UTC time are required.");

        evidence.VerifyStoredEvidence();
        review.VerifyStagedEvidence(evidence);
        // A fresh final-invoice preview captures a NEW CapturedAtUtc
        // and therefore a NEW JSON/SHA even if the physical values
        // are unchanged. The authoritative archived SHA is stable; a
        // separate drift evaluator compares the archived snapshot to
        // the CURRENT persisted Draft using its original capture time.
        // We must not equate two time-dependent snapshot hashes.
        if (driftReview.EvidenceRecordId != evidence.Id ||
            driftReview.DraftInvoiceId != evidence.DraftInvoiceId ||
            driftReview.ContractId != evidence.ContractId ||
            !driftReview.MatchesCurrentDraft ||
            driftReview.Blockers.Any(x =>
                x != "InvoiceIssuanceAndDebtPostingNotImplemented") ||
            preflight.KnownBaseChargeTotal != evidence.KnownBaseChargeTotal ||
            preflight.BaseCharges.Count != 3 ||
            preflight.BaseCharges.Sum(x => x.Amount) !=
                evidence.KnownBaseChargeTotal)
            throw new InvalidOperationException(
                "Current draft has drifted or its archived financial base is inconsistent.");

        var archivedPreflight = new FinalInvoiceIssuePreflight
        {
            MoveOutRequestId = preflight.MoveOutRequestId,
            ContractId = preflight.ContractId,
            RoomId = preflight.RoomId,
            UtilityDraftId = preflight.UtilityDraftId,
            KnownBaseChargeTotal = preflight.KnownBaseChargeTotal,
            BaseCharges = preflight.BaseCharges,
            EvidenceSha256 = evidence.EvidenceSha256
        };
        archivedPreflight.Blockers.AddRange(preflight.Blockers);
        var scope = InvoiceIssuanceScopePolicy.Assess(
            archivedPreflight, scopeFacts);
        var finance = FinalInvoiceFinancialTermsEvaluator.Assess(
            archivedPreflight, approvedTerms);
        if (review.Status != InvoiceIssuanceReviewStatus.ReviewedForDesign ||
            !review.ReviewedAtUtc.HasValue ||
            review.ReviewedAtUtc.Value > issuedAtUtc ||
            evidence.PreparedAtUtc > issuedAtUtc ||
            scopeFacts.AuthenticatedIssuerId != issuerUserId ||
            !scope.IsScopeConsistent ||
            review.BuildingId != scopeFacts.BuildingId ||
            review.StagedEvidenceId != evidence.Id ||
            evidence.MoveOutRequestId != preflight.MoveOutRequestId ||
            evidence.DraftInvoiceId != preflight.UtilityDraftId ||
            evidence.ContractId != preflight.ContractId ||
            evidence.RoomId != preflight.RoomId ||
            provenance.StagedEvidenceId != evidence.Id ||
            !provenance.MatchesPersistedSources ||
            provenance.Blockers.Any(x =>
                x != "OriginalSourcesAuditIsNotIssuanceAuthorization") ||
            finance.Blockers.Any(x =>
                x != "FinancialTermsProposalIsNotPostingAuthorization") ||
            finance.ProposedGrandTotal is not decimal total ||
            finance.VerifiedBaseAmount is not decimal baseAmount ||
            finance.ProposedAuxiliaryAmount is not decimal auxiliaryAmount ||
            baseAmount <= 0m || auxiliaryAmount < 0m || total <= 0m ||
            decimal.Truncate(baseAmount) != baseAmount ||
            decimal.Truncate(auxiliaryAmount) != auxiliaryAmount ||
            decimal.Truncate(total) != total ||
            finance.DocumentedDueDate is not DateOnly dueDate ||
            dueDate < DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(
                    issuedAtUtc,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))))
            throw new InvalidOperationException(
                "Current original sources, issuer scope, documented terms and independent review must all match.");

        var json = JsonSerializer.Serialize(new
        {
            DraftInvoiceId = evidence.DraftInvoiceId,
            EvidenceSha256 = evidence.EvidenceSha256,
            BaseAmount = baseAmount,
            AuxiliaryAmount = auxiliaryAmount,
            TotalAmount = total,
            DueDate = dueDate.ToString("yyyy-MM-dd"),
            DueDatePolicyReference = approvedTerms.DueDatePolicyReference,
            AuxiliaryFeePolicyReference = approvedTerms.AuxiliaryFeePolicyReference,
            AuxiliaryFeeDecision = (int)approvedTerms.AuxiliaryFeeDecision,
            AuxiliaryFeeReviewerId = approvedTerms.AuxiliaryFeeReviewerId,
            AuxiliaryCharges = approvedTerms.AuxiliaryCharges
        });
        if (json.Length > 1_000_000)
            throw new InvalidOperationException("Financial terms exceed the archived size limit.");

        var candidate = new IssuedInvoiceLedgerRecord(id)
        {
            DraftInvoiceId = evidence.DraftInvoiceId,
            StagedEvidenceId = evidence.Id,
            ReviewTicketId = review.Id,
            MoveOutRequestId = evidence.MoveOutRequestId,
            ContractId = evidence.ContractId,
            RoomId = evidence.RoomId,
            BuildingId = review.BuildingId,
            BillingYear = evidence.BillingYear,
            BillingMonth = evidence.BillingMonth,
            InvoiceNumber = invoiceNumber.Trim(),
            IssuedByUserId = issuerUserId,
            IssuedAtUtc = issuedAtUtc,
            DueDate = dueDate,
            BaseAmount = baseAmount,
            AuxiliaryAmount = auxiliaryAmount,
            TotalAmount = total,
            EvidenceSha256 = evidence.EvidenceSha256,
            FinancialTermsJson = json,
            FinancialTermsSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()
        };
        candidate.VerifyStoredTerms(evidence, review);
        return candidate;
    }

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
            var duePolicy = root.GetProperty("DueDatePolicyReference").GetString();
            var feePolicy = root.GetProperty("AuxiliaryFeePolicyReference").GetString();
            var feeReviewer = root.GetProperty("AuxiliaryFeeReviewerId").GetGuid();
            var feeDecision = root.GetProperty("AuxiliaryFeeDecision").GetInt32();
            var charges = root.GetProperty("AuxiliaryCharges");
            if (root.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(duePolicy) || duePolicy.Trim().Length < 8 ||
                duePolicy.Trim().Length > 200 ||
                string.IsNullOrWhiteSpace(feePolicy) || feePolicy.Trim().Length < 8 ||
                feePolicy.Trim().Length > 200 ||
                feeReviewer == Guid.Empty ||
                charges.ValueKind != JsonValueKind.Array ||
                (AuxiliaryAmount == 0m &&
                    (feeDecision != (int)AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges ||
                     charges.GetArrayLength() != 0)) ||
                (AuxiliaryAmount > 0m &&
                    (feeDecision != (int)AuxiliaryFeeAssessmentDecision.ItemizedCharges ||
                     charges.GetArrayLength() == 0)) ||
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
