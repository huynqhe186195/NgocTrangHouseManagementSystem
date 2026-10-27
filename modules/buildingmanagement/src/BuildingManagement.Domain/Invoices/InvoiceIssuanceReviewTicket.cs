using System;
using System.IO;
using System.Linq;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// STAGED two-person review of a prepared invoice evidence snapshot.
/// A reviewed ticket is NEVER an issued invoice or an authorization to post
/// a receivable. No public writer, PostgreSQL migration or live posting
/// transaction currently exists.
/// </summary>
public sealed class InvoiceIssuanceReviewTicket : Entity<Guid>
{
    public Guid StagedEvidenceId { get; private set; }
    public Guid DraftInvoiceId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;
    public Guid SubmittedByUserId { get; private set; }
    public DateTime SubmittedAtUtc { get; private set; }
    public string? SubmissionNote { get; private set; }
    public InvoiceIssuanceReviewStatus Status { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? ReviewReason { get; private set; }

    public bool CanIssue => false;
    public bool CanPostReceivable => false;
    public bool CanReduceDebt => false;

    private InvoiceIssuanceReviewTicket() { }

    private InvoiceIssuanceReviewTicket(
        Guid id, FinalInvoiceIssueEvidenceRecord archived, Guid buildingId,
        Guid submittedByUserId, DateTime submittedAtUtc,
        string? submissionNote) : base(id)
    {
        StagedEvidenceId = archived.Id;
        DraftInvoiceId = archived.DraftInvoiceId;
        MoveOutRequestId = archived.MoveOutRequestId;
        ContractId = archived.ContractId;
        RoomId = archived.RoomId;
        BuildingId = buildingId;
        EvidenceSha256 = archived.EvidenceSha256;
        SubmittedByUserId = submittedByUserId;
        SubmittedAtUtc = submittedAtUtc;
        SubmissionNote = string.IsNullOrWhiteSpace(submissionNote)
            ? null : submissionNote.Trim();
        Status = InvoiceIssuanceReviewStatus.Submitted;
    }

    /// <summary>
    /// Offline model preparation only. The building is not inferred from a
    /// client-provided value in any production operation. A future service
    /// MUST resolve building/resource membership from authenticated DB facts.
    /// </summary>
    public static InvoiceIssuanceReviewTicket Stage(
        Guid id, FinalInvoiceIssueEvidenceRecord archived,
        Guid buildingId, Guid submittedByUserId, DateTime submittedAtUtc,
        string? submissionNote = null)
    {
        ArgumentNullException.ThrowIfNull(archived);
        if (id == Guid.Empty || archived.Id == Guid.Empty ||
            buildingId == Guid.Empty || submittedByUserId == Guid.Empty)
            throw new ArgumentException("Review, evidence, building and submitter IDs are required.");
        if (submittedAtUtc == default ||
            submittedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Submission must be UTC.", nameof(submittedAtUtc));
        if (submissionNote?.Trim().Length > 500)
            throw new ArgumentException("Submission note is too long.", nameof(submissionNote));

        archived.VerifyStoredEvidence();
        return new InvoiceIssuanceReviewTicket(
            id, archived, buildingId, submittedByUserId,
            submittedAtUtc, submissionNote);
    }

    /// <summary>
    /// Records a reviewer sign-off FOR DESIGN ONLY. Scope facts and preflight
    /// MUST be freshly resolved server-side; passing them to this domain
    /// method is not a substitute for a future lock + authorization check.
    /// A reviewer cannot approve their own submission or replay a decision.
    /// </summary>
    public void RecordReviewForDesign(
        Guid reviewerUserId, DateTime reviewedAtUtc, bool acceptForDesign,
        string reason, FinalInvoiceIssuePreflight preflight,
        InvoiceIssuanceScopeFacts scopeFacts)
    {
        if (Status != InvoiceIssuanceReviewStatus.Submitted)
            throw new InvalidOperationException("This review was already decided.");
        if (reviewerUserId == Guid.Empty || reviewerUserId == SubmittedByUserId)
            throw new InvalidOperationException("An independent reviewer is required.");
        if (reviewedAtUtc == default ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            reviewedAtUtc < SubmittedAtUtc)
            throw new ArgumentException("Review must have a valid UTC timestamp.", nameof(reviewedAtUtc));
        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length < 8 || reason.Trim().Length > 500)
            throw new ArgumentException("A documented review reason of 8-500 characters is required.", nameof(reason));
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(scopeFacts);

        var scope = InvoiceIssuanceScopePolicy.Assess(preflight, scopeFacts);
        if (!scope.IsScopeConsistent ||
            scopeFacts.AuthenticatedIssuerId != reviewerUserId ||
            scopeFacts.BuildingId != BuildingId ||
            preflight.MoveOutRequestId != MoveOutRequestId ||
            preflight.ContractId != ContractId ||
            preflight.RoomId != RoomId ||
            preflight.UtilityDraftId != DraftInvoiceId ||
            !StringComparer.Ordinal.Equals(preflight.EvidenceSha256, EvidenceSha256) ||
            !StringComparer.Ordinal.Equals(
                scopeFacts.PersistedEvidenceSha256, EvidenceSha256))
            throw new InvalidOperationException(
                "Reviewer scope or staged invoice evidence does not match.");

        // A design-only signoff may acknowledge the known, unavoidable
        // future-policy blockers. It must NOT label abnormal/missing
        // monetary evidence as reviewed successfully. Rejection remains
        // allowed so the reviewer can document why the evidence is bad.
        if (acceptForDesign && preflight.Blockers.Any(blocker =>
                blocker is not "AuxiliaryFeePolicyUndecided" and
                    not "InvoiceDueDatePolicyUndecided" and
                    not "IssuerBuildingAuthorizationUnavailable" and
                    not "ImmutableIssuedInvoiceLedgerNotImplemented" and
                    not "AuditedPaymentAllocationNotImplemented"))
            throw new InvalidOperationException(
                "Invoice evidence anomalies must be resolved before design signoff.");

        ReviewedByUserId = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc;
        ReviewReason = reason.Trim();
        Status = acceptForDesign
            ? InvoiceIssuanceReviewStatus.ReviewedForDesign
            : InvoiceIssuanceReviewStatus.Rejected;
    }

    /// <summary>
    /// Check persisted review metadata against its staged snapshot.
    /// A checksum is integrity evidence, not a signature or legal approval.
    /// </summary>
    public void VerifyStagedEvidence(FinalInvoiceIssueEvidenceRecord archived)
    {
        ArgumentNullException.ThrowIfNull(archived);
        archived.VerifyStoredEvidence();
        if (StagedEvidenceId == Guid.Empty || BuildingId == Guid.Empty ||
            SubmittedByUserId == Guid.Empty || SubmittedAtUtc == default ||
            archived.Id != StagedEvidenceId ||
            archived.DraftInvoiceId != DraftInvoiceId ||
            archived.MoveOutRequestId != MoveOutRequestId ||
            archived.ContractId != ContractId ||
            archived.RoomId != RoomId ||
            !StringComparer.Ordinal.Equals(archived.EvidenceSha256, EvidenceSha256) ||
            !Enum.IsDefined(Status) ||
            (Status == InvoiceIssuanceReviewStatus.Submitted &&
                (ReviewedByUserId.HasValue || ReviewedAtUtc.HasValue ||
                 !string.IsNullOrEmpty(ReviewReason))) ||
            (Status != InvoiceIssuanceReviewStatus.Submitted &&
                (!ReviewedByUserId.HasValue ||
                 ReviewedByUserId == Guid.Empty ||
                 ReviewedByUserId == SubmittedByUserId ||
                 !ReviewedAtUtc.HasValue ||
                 ReviewedAtUtc.Value < SubmittedAtUtc ||
                 string.IsNullOrWhiteSpace(ReviewReason) ||
                 ReviewReason.Length < 8 || ReviewReason.Length > 500)))
            throw new InvalidDataException(
                "Persisted review ticket and invoice evidence are inconsistent.");
    }
}

public enum InvoiceIssuanceReviewStatus
{
    Submitted = 1,
    ReviewedForDesign = 2,
    Rejected = 3
}
