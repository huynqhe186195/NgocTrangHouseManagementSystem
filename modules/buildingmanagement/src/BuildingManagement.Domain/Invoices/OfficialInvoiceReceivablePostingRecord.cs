using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.Payments;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// A separately posted, immutable receivable. Source issuance remains
/// unchanged; posting is an auditable event, not a mutation to its Draft.
/// No automatic deposit offset or automatic bank verification is implied.
/// </summary>
public enum OfficialInvoiceKind { Monthly = 1, Final = 2 }

public sealed class OfficialInvoiceReceivablePostingRecord : Entity<Guid>
{
    public OfficialInvoiceKind Kind { get; private set; }
    public Guid IssuedInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public int BillingYear { get; private set; }
    public int BillingMonth { get; private set; }
    public decimal OriginalAmount { get; private set; }
    public DateOnly DueDate { get; private set; }
    public string SourceEvidenceSha256 { get; private set; } = string.Empty;
    public Guid PostedByUserId { get; private set; }
    public DateTime PostedAtUtc { get; private set; }

    public bool IsOfficialReceivable => true;
    public bool CanOffsetDeposit => false;

    private OfficialInvoiceReceivablePostingRecord() { }
    private OfficialInvoiceReceivablePostingRecord(Guid id) : base(id) { }

    public static OfficialInvoiceReceivablePostingRecord PostMonthly(
        Guid id, MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceCandidateLedgerRecord source,
        MonthlyInvoiceIndependentReviewRecord review,
        Guid postingUserId, DateTime postedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(issued);
        issued.VerifyAgainst(source, review);
        return Create(id, OfficialInvoiceKind.Monthly, issued.Id,
            issued.ContractId, issued.RoomId, issued.BuildingId,
            issued.BillingYear, issued.BillingMonth, issued.TotalAmount,
            issued.DueDate, issued.CandidateSha256, issued.IssuedAtUtc,
            postingUserId, postedAtUtc);
    }

    public static OfficialInvoiceReceivablePostingRecord PostFinal(
        Guid id, IssuedInvoiceLedgerRecord issued,
        FinalInvoiceIssueEvidenceRecord source,
        InvoiceIssuanceReviewTicket review,
        Guid postingUserId, DateTime postedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(issued);
        issued.VerifyStoredTerms(source, review);
        return Create(id, OfficialInvoiceKind.Final, issued.Id,
            issued.ContractId, issued.RoomId, issued.BuildingId,
            issued.BillingYear, issued.BillingMonth, issued.TotalAmount,
            issued.DueDate, issued.FinancialTermsSha256, issued.IssuedAtUtc,
            postingUserId, postedAtUtc);
    }

    private static OfficialInvoiceReceivablePostingRecord Create(
        Guid id, OfficialInvoiceKind kind, Guid issuedId,
        Guid contract, Guid room, Guid building, int year, int month,
        decimal amount, DateOnly due, string hash, DateTime issuedAt,
        Guid actor, DateTime postedAt)
    {
        if (id == Guid.Empty || issuedId == Guid.Empty ||
            contract == Guid.Empty || room == Guid.Empty ||
            building == Guid.Empty || actor == Guid.Empty ||
            !Enum.IsDefined(kind) || year is < 2000 or > 2200 ||
            month is < 1 or > 12 || amount <= 0m ||
            decimal.Truncate(amount) != amount || due == default ||
            string.IsNullOrWhiteSpace(hash) || hash.Length != 64 ||
            issuedAt.Kind != DateTimeKind.Utc ||
            postedAt.Kind != DateTimeKind.Utc || postedAt < issuedAt)
            throw new InvalidOperationException(
                "Posting requires validated issued source, currency and authenticated UTC actor.");
        return new OfficialInvoiceReceivablePostingRecord(id)
        {
            Kind = kind, IssuedInvoiceId = issuedId,
            ContractId = contract, RoomId = room, BuildingId = building,
            BillingYear = year, BillingMonth = month, OriginalAmount = amount,
            DueDate = due, SourceEvidenceSha256 = hash,
            PostedByUserId = actor,
            PostedAtUtc = postedAt.AddTicks(-(postedAt.Ticks % 10L))
        };
    }

    public void VerifyMonthly(MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceCandidateLedgerRecord source,
        MonthlyInvoiceIndependentReviewRecord review)
    {
        issued.VerifyAgainst(source, review);
        Verify(OfficialInvoiceKind.Monthly, issued.Id, issued.ContractId,
            issued.RoomId, issued.BuildingId, issued.BillingYear,
            issued.BillingMonth, issued.TotalAmount, issued.DueDate,
            issued.CandidateSha256, issued.IssuedAtUtc);
    }

    public void VerifyFinal(IssuedInvoiceLedgerRecord issued,
        FinalInvoiceIssueEvidenceRecord source,
        InvoiceIssuanceReviewTicket review)
    {
        issued.VerifyStoredTerms(source, review);
        Verify(OfficialInvoiceKind.Final, issued.Id, issued.ContractId,
            issued.RoomId, issued.BuildingId, issued.BillingYear,
            issued.BillingMonth, issued.TotalAmount, issued.DueDate,
            issued.FinancialTermsSha256, issued.IssuedAtUtc);
    }

    private void Verify(OfficialInvoiceKind kind, Guid id, Guid contract,
        Guid room, Guid building, int year, int month, decimal amount,
        DateOnly due, string hash, DateTime issuedAt)
    {
        if (Id == Guid.Empty || Kind != kind || IssuedInvoiceId != id ||
            ContractId != contract || RoomId != room ||
            BuildingId != building || BillingYear != year ||
            BillingMonth != month || OriginalAmount != amount ||
            DueDate != due || SourceEvidenceSha256 != hash ||
            PostedByUserId == Guid.Empty || PostedAtUtc.Kind != DateTimeKind.Utc ||
            PostedAtUtc < issuedAt)
            throw new InvalidDataException(
                "Official receivable no longer matches immutable issued evidence.");
    }
}

/// <summary>
/// A trusted server-side mapping of one original Payment to its immutable
/// staging allocation. Construct only from a materialized receipt and ledger.
/// </summary>
public sealed record OfficialInvoiceReceiptEvidence(
    Guid AllocationId, Guid PaymentId, Guid IssuedInvoiceId,
    Guid ContractId, decimal Amount, DateTime PaidAtUtc,
    Guid RecordedByUserId, DateTime RecordedAtUtc,
    string BankTransactionReference, string OriginalProof)
{
    public static OfficialInvoiceReceiptEvidence FromMonthly(
        MonthlyInvoicePaymentAllocationRecord journal, Payment receipt)
    {
        journal.VerifySource(receipt);
        return Create(journal.Id, journal.PaymentId, journal.IssuedInvoiceId,
            journal.ContractId, journal.Amount, journal.PaidAtUtc,
            journal.RecordedByUserId, journal.RecordedAtUtc, receipt);
    }

    public static OfficialInvoiceReceiptEvidence FromFinal(
        InvoicePaymentAllocationJournalEntry journal, Payment receipt)
    {
        journal.VerifySourceReceipt(receipt);
        return Create(journal.Id, journal.PaymentId, journal.IssuedInvoiceId,
            journal.ContractId, journal.Amount, journal.PaymentPaidAtUtc,
            journal.RecordedByUserId, journal.RecordedAtUtc, receipt);
    }

    private static OfficialInvoiceReceiptEvidence Create(
        Guid allocationId, Guid paymentId, Guid issued, Guid contract,
        decimal amount, DateTime paidAt, Guid recorder, DateTime recordedAt,
        Payment receipt)
    {
        if (allocationId == Guid.Empty || paymentId == Guid.Empty ||
            issued == Guid.Empty || contract == Guid.Empty ||
            recorder == Guid.Empty || amount <= 0m ||
            decimal.Truncate(amount) != amount ||
            paidAt.Kind != DateTimeKind.Utc ||
            recordedAt.Kind != DateTimeKind.Utc ||
            recordedAt < paidAt || receipt.Method !=
                BuildingManagement.Payments.BuildingManagement.Payments.PaymentMethod.BankTransfer ||
            receipt.Channel != PaymentChannel.Manual ||
            string.IsNullOrWhiteSpace(receipt.TransactionReference) ||
            receipt.TransactionReference.Trim().Length is < 8 or > 128 ||
            string.IsNullOrWhiteSpace(receipt.Notes) ||
            !receipt.Notes.StartsWith("BANK-PROOF: ", StringComparison.Ordinal) ||
            receipt.Notes.Length < 20)
            throw new InvalidDataException(
                "Receipt requires immutable completed bank evidence and a matching journal.");
        return new OfficialInvoiceReceiptEvidence(allocationId, paymentId,
            issued, contract, amount, paidAt, recorder, recordedAt,
            receipt.TransactionReference.Trim(), receipt.Notes);
    }
}

/// <summary>
/// Two-person review of a manually-attested bank transfer. ACCEPT means an
/// authenticated human reviewed the bank statement evidence; it does NOT
/// claim any live bank API/gateway verification.
/// </summary>
public sealed class OfficialInvoiceReceiptReviewRecord : Entity<Guid>
{
    public Guid ReceivablePostingId { get; private set; }
    public Guid ReceiptAllocationId { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid ContractId { get; private set; }
    public decimal Amount { get; private set; }
    public string BankTransactionReference { get; private set; } = string.Empty;
    public string BankStatementEvidenceReference { get; private set; } = string.Empty;
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public bool Accepted { get; private set; }
    public string ReviewReason { get; private set; } = string.Empty;

    public bool IsManuallyVerifiedBankEvidence => Accepted;
    public bool IsExternallyVerifiedBankTransfer => false;
    public bool CanOffsetDeposit => false;

    private OfficialInvoiceReceiptReviewRecord() { }
    private OfficialInvoiceReceiptReviewRecord(Guid id) : base(id) { }

    public static OfficialInvoiceReceiptReviewRecord Review(
        Guid id, OfficialInvoiceReceivablePostingRecord posted,
        OfficialInvoiceReceiptEvidence evidence, Guid reviewer,
        DateTime reviewedAtUtc, bool accepted,
        string statementReference, string reason)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(evidence);
        var reference = statementReference?.Trim() ?? string.Empty;
        var why = reason?.Trim() ?? string.Empty;
        if (id == Guid.Empty || posted.Id == Guid.Empty ||
            evidence.AllocationId == Guid.Empty ||
            evidence.PaymentId == Guid.Empty ||
            evidence.IssuedInvoiceId != posted.IssuedInvoiceId ||
            evidence.ContractId != posted.ContractId ||
            reviewer == Guid.Empty ||
            reviewer == evidence.RecordedByUserId ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            reviewedAtUtc < evidence.RecordedAtUtc ||
            reviewedAtUtc < posted.PostedAtUtc ||
            why.Length is < 12 or > 500 ||
            (accepted && reference.Length is < 8 or > 200) ||
            (!accepted && reference.Length > 200))
            throw new InvalidOperationException(
                "A separate authenticated reviewer and documented bank evidence are required.");
        return new OfficialInvoiceReceiptReviewRecord(id)
        {
            ReceivablePostingId = posted.Id,
            ReceiptAllocationId = evidence.AllocationId,
            PaymentId = evidence.PaymentId,
            ContractId = posted.ContractId,
            Amount = evidence.Amount,
            BankTransactionReference = evidence.BankTransactionReference,
            BankStatementEvidenceReference = reference,
            ReviewedByUserId = reviewer,
            ReviewedAtUtc = reviewedAtUtc.AddTicks(-(reviewedAtUtc.Ticks % 10L)),
            Accepted = accepted, ReviewReason = why
        };
    }

    public void Verify(OfficialInvoiceReceivablePostingRecord posted,
        OfficialInvoiceReceiptEvidence evidence)
    {
        if (posted is null || evidence is null ||
            Id == Guid.Empty || ReceivablePostingId != posted.Id ||
            ReceiptAllocationId != evidence.AllocationId ||
            PaymentId != evidence.PaymentId ||
            ContractId != posted.ContractId ||
            evidence.ContractId != posted.ContractId ||
            evidence.IssuedInvoiceId != posted.IssuedInvoiceId ||
            Amount != evidence.Amount ||
            BankTransactionReference != evidence.BankTransactionReference ||
            ReviewedByUserId == Guid.Empty ||
            ReviewedByUserId == evidence.RecordedByUserId ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewedAtUtc < evidence.RecordedAtUtc ||
            ReviewedAtUtc < posted.PostedAtUtc ||
            ReviewReason?.Trim().Length is < 12 or > 500 ||
            (Accepted && BankStatementEvidenceReference?.Trim().Length is < 8 or > 200))
            throw new InvalidDataException(
                "Manual receipt review differs from original bank and posting evidence.");
    }
}

/// <summary>
/// Official outstanding: posted receivable + independently approved notes
/// minus independently reviewed, completed bank transfer records. Unreviewed
/// manual receipts NEVER reduce official debt. No deposit can be applied.
/// </summary>
public static class OfficialInvoiceReceivableBalanceEvaluator
{
    public static OfficialInvoiceReceivableBalance Assess(
        OfficialInvoiceReceivablePostingRecord posted, DateOnly asOfDate,
        decimal approvedSignedAdjustment,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence> allReceipts,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord> allReviews)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(allReceipts);
        ArgumentNullException.ThrowIfNull(allReviews);
        if (asOfDate == default ||
            decimal.Truncate(approvedSignedAdjustment) != approvedSignedAdjustment)
            throw new ArgumentException("Valid date and whole-VND audited adjustment required.");
        var receipts = allReceipts.ToArray();
        var reviews = allReviews.ToArray();
        if (receipts.Any(x => x is null) || reviews.Any(x => x is null) ||
            receipts.Select(x => x.PaymentId).Distinct().Count() != receipts.Length ||
            receipts.Select(x => x.AllocationId).Distinct().Count() != receipts.Length ||
            reviews.Select(x => x.ReceiptAllocationId).Distinct().Count() != reviews.Length)
            throw new InvalidDataException("Duplicate or missing receipt evidence.");
        if (posted.Kind == OfficialInvoiceKind.Final &&
            approvedSignedAdjustment != 0m)
            throw new InvalidDataException("Final invoice has no approved correction ledger.");
        var map = receipts.ToDictionary(x => x.AllocationId);
        foreach (var receipt in receipts)
            if (receipt.IssuedInvoiceId != posted.IssuedInvoiceId ||
                receipt.ContractId != posted.ContractId)
                throw new InvalidDataException("Receipt belongs to another invoice.");
        foreach (var review in reviews)
        {
            if (!map.TryGetValue(review.ReceiptAllocationId, out var receipt))
                throw new InvalidDataException("Orphaned manual bank approval.");
            review.Verify(posted, receipt);
        }
        var adjusted = checked(posted.OriginalAmount + approvedSignedAdjustment);
        if (adjusted <= 0m)
            throw new InvalidDataException("Posted receivable adjustment cannot erase its invoice.");
        var timezone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        DateOnly BusinessDate(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timezone));
        var accepted = reviews.Where(x => x.Accepted &&
            BusinessDate(x.ReviewedAtUtc) <= asOfDate &&
            BusinessDate(map[x.ReceiptAllocationId].PaidAtUtc) <= asOfDate).ToArray();
        decimal paid = 0m;
        foreach (var review in accepted) paid = checked(paid + review.Amount);
        if (paid > adjusted)
            throw new InvalidDataException("Verified receipts exceed approved receivable.");
        var outstanding = adjusted - paid;
        var status = outstanding == 0m ? IssuedInvoicePaymentStatus.Paid :
            asOfDate > posted.DueDate ? IssuedInvoicePaymentStatus.Overdue :
            paid > 0m ? IssuedInvoicePaymentStatus.PartiallyPaid :
            IssuedInvoicePaymentStatus.Unpaid;
        return new OfficialInvoiceReceivableBalance(posted.Id,
            posted.IssuedInvoiceId, posted.ContractId, posted.Kind,
            posted.OriginalAmount, approvedSignedAdjustment, adjusted,
            paid, outstanding, accepted.Length,
            receipts.Length - accepted.Length, status, asOfDate,
            IsOfficialDebtPosted: true,
            IsExternallyVerifiedBankTransfer: false);
    }
}

public sealed record OfficialInvoiceReceivableBalance(
    Guid ReceivablePostingId, Guid IssuedInvoiceId, Guid ContractId,
    OfficialInvoiceKind Kind, decimal OriginalAmount,
    decimal ApprovedSignedAdjustment, decimal AdjustedAmount,
    decimal IndependentlyReviewedManualPayments, decimal OutstandingAmount,
    int AcceptedPaymentCount, int PendingOrRejectedPaymentCount,
    IssuedInvoicePaymentStatus Status, DateOnly AsOfDate,
    bool IsOfficialDebtPosted, bool IsExternallyVerifiedBankTransfer)
{
    public bool CanOffsetDeposit => false;
}
