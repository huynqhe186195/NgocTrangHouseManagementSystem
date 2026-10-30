using System;
using System.IO;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Independent, immutable financial review of one verified monthly archive.
/// The preparer and reviewer MUST be different authenticated users. A
/// rejected review is retained, not silently converted to approved.
/// </summary>
public sealed class MonthlyInvoiceIndependentReviewRecord : Entity<Guid>
{
    public Guid CandidateId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public string CandidateSha256 { get; private set; } = string.Empty;
    public Guid PreparedByUserId { get; private set; }
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public bool Accepted { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    private MonthlyInvoiceIndependentReviewRecord() { }
    private MonthlyInvoiceIndependentReviewRecord(Guid id) : base(id) { }

    public static MonthlyInvoiceIndependentReviewRecord Capture(
        Guid id, MonthlyInvoiceCandidateLedgerRecord candidate,
        Guid reviewerId, DateTime reviewedAtUtc,
        bool accepted, string reason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.VerifyStoredEvidence();
        var note = reason?.Trim() ?? string.Empty;
        if (id == Guid.Empty || reviewerId == Guid.Empty ||
            reviewerId == candidate.ReviewedByUserId ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            reviewedAtUtc < candidate.ReviewedAtUtc ||
            note.Length is < 8 or > 500)
            throw new InvalidOperationException(
                "An independent named reviewer, UTC timestamp and documented decision are required.");
        var normalized = reviewedAtUtc.AddTicks(-(reviewedAtUtc.Ticks % 10L));
        return new MonthlyInvoiceIndependentReviewRecord(id)
        {
            CandidateId = candidate.Id,
            ContractId = candidate.ContractId,
            BuildingId = candidate.BuildingId,
            CandidateSha256 = candidate.FrozenEvidenceSha256,
            PreparedByUserId = candidate.ReviewedByUserId,
            ReviewedByUserId = reviewerId,
            ReviewedAtUtc = normalized,
            Accepted = accepted,
            Reason = note
        };
    }

    public void VerifyAgainst(MonthlyInvoiceCandidateLedgerRecord candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.VerifyStoredEvidence();
        if (Id == Guid.Empty || CandidateId != candidate.Id ||
            ContractId != candidate.ContractId ||
            BuildingId != candidate.BuildingId ||
            !StringComparer.Ordinal.Equals(
                CandidateSha256, candidate.FrozenEvidenceSha256) ||
            PreparedByUserId != candidate.ReviewedByUserId ||
            PreparedByUserId == Guid.Empty || ReviewedByUserId == Guid.Empty ||
            PreparedByUserId == ReviewedByUserId ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewedAtUtc < candidate.ReviewedAtUtc ||
            string.IsNullOrWhiteSpace(Reason) ||
            Reason.Trim().Length is < 8 or > 500)
            throw new InvalidDataException(
                "Independent monthly financial review is not consistent with original evidence.");
    }
}

/// <summary>
/// One immutable monthly issuance EVENT. Prepared financial values are
/// captured solely by reference to the SHA-verified archive. This is an
/// authenticated staging-issued document, not a recognized receivable or
/// a claim that external bank funds were independently verified.
/// </summary>
public sealed class MonthlyIssuedInvoiceRecord : Entity<Guid>
{
    public Guid CandidateId { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid DraftInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public string InvoiceNumber { get; private set; } = string.Empty;
    public Guid IssuedByUserId { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string CandidateSha256 { get; private set; } = string.Empty;

    public bool IsStagingIssued => true;
    public bool IsOfficialReceivable => false;
    public bool HasIndependentlyVerifiedBankReceipt => false;

    private MonthlyIssuedInvoiceRecord() { }
    private MonthlyIssuedInvoiceRecord(Guid id) : base(id) { }

    public static MonthlyIssuedInvoiceRecord Issue(
        Guid id, string invoiceNumber, Guid issuerId, DateTime issuedAtUtc,
        MonthlyInvoiceCandidateLedgerRecord candidate,
        MonthlyInvoiceIndependentReviewRecord review)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(review);
        review.VerifyAgainst(candidate);
        var number = invoiceNumber?.Trim() ?? string.Empty;
        if (!review.Accepted || id == Guid.Empty ||
            issuerId == Guid.Empty || issuerId == review.ReviewedByUserId ||
            issuedAtUtc.Kind != DateTimeKind.Utc ||
            issuedAtUtc < review.ReviewedAtUtc ||
            number.Length is < 5 or > 64 ||
            candidate.TotalAmount <= 0m)
            throw new InvalidOperationException(
                "Approved independent review, distinct issuer and valid frozen amount required.");
        var normalized = issuedAtUtc.AddTicks(-(issuedAtUtc.Ticks % 10L));
        if (candidate.DueDate < DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(normalized,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))))
            throw new InvalidOperationException(
                "The original approved monthly payment due date has expired.");
        return new MonthlyIssuedInvoiceRecord(id)
        {
            CandidateId = candidate.Id,
            ReviewId = review.Id,
            DraftInvoiceId = candidate.DraftInvoiceId,
            ContractId = candidate.ContractId,
            RoomId = candidate.RoomId,
            BuildingId = candidate.BuildingId,
            BillingYear = candidate.BillingYear,
            BillingMonth = candidate.BillingMonth,
            InvoiceNumber = number,
            IssuedByUserId = issuerId,
            IssuedAtUtc = normalized,
            DueDate = candidate.DueDate,
            TotalAmount = candidate.TotalAmount,
            CandidateSha256 = candidate.FrozenEvidenceSha256
        };
    }

    public void VerifyAgainst(
        MonthlyInvoiceCandidateLedgerRecord candidate,
        MonthlyInvoiceIndependentReviewRecord review)
    {
        review.VerifyAgainst(candidate);
        if (Id == Guid.Empty || CandidateId != candidate.Id ||
            ReviewId != review.Id || !review.Accepted ||
            DraftInvoiceId != candidate.DraftInvoiceId ||
            ContractId != candidate.ContractId ||
            RoomId != candidate.RoomId || BuildingId != candidate.BuildingId ||
            BillingYear != candidate.BillingYear ||
            BillingMonth != candidate.BillingMonth ||
            TotalAmount != candidate.TotalAmount || DueDate != candidate.DueDate ||
            CandidateSha256 != candidate.FrozenEvidenceSha256 ||
            IssuedByUserId == Guid.Empty ||
            IssuedByUserId == review.ReviewedByUserId ||
            IssuedAtUtc.Kind != DateTimeKind.Utc ||
            IssuedAtUtc < review.ReviewedAtUtc ||
            // The creation gate also rejects an already-expired due date.
            // Apply that invariant when loading persisted evidence, otherwise
            // a rewritten IssuedAtUtc could bypass the historical guard.
            DueDate < DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(IssuedAtUtc,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))) ||
            string.IsNullOrWhiteSpace(InvoiceNumber) ||
            InvoiceNumber.Length is < 5 or > 64)
            throw new InvalidDataException(
                "Monthly issued invoice no longer matches archived, independently approved terms.");
    }
}
