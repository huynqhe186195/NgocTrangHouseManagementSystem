using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Proposed credit/debit note against immutable staging monthly issuance.
/// The original invoice, meter evidence and bank receipts never change.
/// A proposal has NO effect until a separate independent approval exists.
/// </summary>
public sealed class MonthlyInvoiceAdjustmentRecord : Entity<Guid>
{
    public Guid IssuedInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public string InvoiceEvidenceSha256 { get; private set; } = string.Empty;
    public decimal SignedDeltaAmount { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string SourceEvidenceReference { get; private set; } = string.Empty;
    public Guid ProposedByUserId { get; private set; }
    public DateTime ProposedAtUtc { get; private set; }

    public bool IsCreditNote => SignedDeltaAmount < 0m;
    public bool IsDebitNote => SignedDeltaAmount > 0m;
    public bool IsOfficialDebtAdjustment => false;

    private MonthlyInvoiceAdjustmentRecord() { }
    private MonthlyInvoiceAdjustmentRecord(Guid id) : base(id) { }

    public static MonthlyInvoiceAdjustmentRecord Propose(
        Guid id, MonthlyIssuedInvoiceRecord issued, Guid proposerId,
        DateTime proposedAtUtc, decimal signedDeltaAmount, string reason,
        string sourceEvidenceReference)
    {
        ArgumentNullException.ThrowIfNull(issued);
        var why = reason?.Trim() ?? string.Empty;
        var evidence = sourceEvidenceReference?.Trim() ?? string.Empty;
        if (id == Guid.Empty || issued.Id == Guid.Empty ||
            proposerId == Guid.Empty ||
            proposedAtUtc.Kind != DateTimeKind.Utc ||
            proposedAtUtc < issued.IssuedAtUtc ||
            signedDeltaAmount == 0m ||
            decimal.Truncate(signedDeltaAmount) != signedDeltaAmount ||
            why.Length is < 12 or > 500 ||
            evidence.Length is < 8 or > 200 ||
            string.IsNullOrWhiteSpace(issued.CandidateSha256))
            throw new InvalidOperationException(
                "Credit/debit proposal needs an issued source, whole VND, reason and evidence.");
        return new MonthlyInvoiceAdjustmentRecord(id)
        {
            IssuedInvoiceId = issued.Id,
            ContractId = issued.ContractId,
            BuildingId = issued.BuildingId,
            InvoiceEvidenceSha256 = issued.CandidateSha256,
            SignedDeltaAmount = signedDeltaAmount,
            Reason = why,
            SourceEvidenceReference = evidence,
            ProposedByUserId = proposerId,
            ProposedAtUtc = Normalize(proposedAtUtc)
        };
    }

    public void VerifyAgainst(MonthlyIssuedInvoiceRecord issued)
    {
        if (issued is null || Id == Guid.Empty ||
            IssuedInvoiceId != issued.Id ||
            ContractId != issued.ContractId ||
            BuildingId != issued.BuildingId ||
            !StringComparer.Ordinal.Equals(
                InvoiceEvidenceSha256, issued.CandidateSha256) ||
            ProposedByUserId == Guid.Empty ||
            ProposedAtUtc.Kind != DateTimeKind.Utc ||
            ProposedAtUtc < issued.IssuedAtUtc ||
            SignedDeltaAmount == 0m ||
            decimal.Truncate(SignedDeltaAmount) != SignedDeltaAmount ||
            string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length is < 12 or > 500 ||
            string.IsNullOrWhiteSpace(SourceEvidenceReference) ||
            SourceEvidenceReference.Trim().Length is < 8 or > 200)
            throw new InvalidDataException("Monthly adjustment proposal has inconsistent source evidence.");
    }

    internal static DateTime Normalize(DateTime value)
        => value.AddTicks(-(value.Ticks % 10L));
}

/// <summary>
/// Immutable independent decision; rejected proposals are never posted.
/// Financial effects are derived from accepted decisions, not mutable status.
/// </summary>
public sealed class MonthlyInvoiceAdjustmentDecisionRecord : Entity<Guid>
{
    public Guid AdjustmentId { get; private set; }
    public Guid IssuedInvoiceId { get; private set; }
    public Guid ContractId { get; private set; }
    public decimal ApprovedSignedDeltaAmount { get; private set; }
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public bool Accepted { get; private set; }
    public string ReviewReason { get; private set; } = string.Empty;

    public bool IsOfficialDebtPosting => false;

    private MonthlyInvoiceAdjustmentDecisionRecord() { }
    private MonthlyInvoiceAdjustmentDecisionRecord(Guid id) : base(id) { }

    public static MonthlyInvoiceAdjustmentDecisionRecord Decide(
        Guid id, MonthlyInvoiceAdjustmentRecord proposal,
        MonthlyIssuedInvoiceRecord issued,
        Guid reviewerId, DateTime reviewedAtUtc,
        bool accepted, string reviewReason)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        proposal.VerifyAgainst(issued);
        var why = reviewReason?.Trim() ?? string.Empty;
        if (id == Guid.Empty || reviewerId == Guid.Empty ||
            reviewerId == proposal.ProposedByUserId ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            reviewedAtUtc < proposal.ProposedAtUtc ||
            why.Length is < 12 or > 500)
            throw new InvalidOperationException(
                "Adjustment must be independently reviewed with documented reason.");
        return new MonthlyInvoiceAdjustmentDecisionRecord(id)
        {
            AdjustmentId = proposal.Id,
            IssuedInvoiceId = issued.Id,
            ContractId = issued.ContractId,
            ApprovedSignedDeltaAmount = accepted
                ? proposal.SignedDeltaAmount : 0m,
            ReviewedByUserId = reviewerId,
            ReviewedAtUtc = MonthlyInvoiceAdjustmentRecord.Normalize(reviewedAtUtc),
            Accepted = accepted,
            ReviewReason = why
        };
    }

    public void VerifyAgainst(
        MonthlyInvoiceAdjustmentRecord proposal, MonthlyIssuedInvoiceRecord issued)
    {
        proposal.VerifyAgainst(issued);
        if (Id == Guid.Empty || AdjustmentId != proposal.Id ||
            IssuedInvoiceId != issued.Id ||
            ContractId != issued.ContractId ||
            ReviewedByUserId == Guid.Empty ||
            ReviewedByUserId == proposal.ProposedByUserId ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewedAtUtc < proposal.ProposedAtUtc ||
            ReviewReason.Trim().Length is < 12 or > 500 ||
            ApprovedSignedDeltaAmount !=
                (Accepted ? proposal.SignedDeltaAmount : 0m))
            throw new InvalidDataException("Monthly adjustment decision has drifted from immutable proposal.");
    }
}

/// <summary>
/// Evaluates the recognized-looking amount only as a STAGING projection.
/// Entire history is validated before date filtering; a rejected decision
/// cannot alter the amount, nor may credits create negative net amounts
/// or reduce amounts below already captured receipts.
/// </summary>
public static class MonthlyInvoiceAdjustmentEvaluator
{
    public static MonthlyInvoiceAdjustmentSummary Assess(
        MonthlyIssuedInvoiceRecord issued,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord> proposals,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord> decisions,
        DateOnly asOfDate,
        decimal completedReceiptsAsOf)
    {
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(decisions);
        if (asOfDate == default || completedReceiptsAsOf < 0m)
            throw new ArgumentException("Valid as-of date and nonnegative receipt amount required.");

        var original = proposals.ToArray();
        var approvals = decisions.ToArray();
        if (original.Any(x => x is null) || approvals.Any(x => x is null) ||
            original.Select(x => x.Id).Distinct().Count() != original.Length ||
            approvals.Select(x => x.AdjustmentId).Distinct().Count() != approvals.Length)
            throw new InvalidDataException("Duplicate or missing monthly adjustment audit record.");
        var byId = original.ToDictionary(x => x.Id);
        foreach (var p in original) p.VerifyAgainst(issued);
        foreach (var d in approvals)
        {
            if (!byId.TryGetValue(d.AdjustmentId, out var p))
                throw new InvalidDataException("Orphaned adjustment approval");
            d.VerifyAgainst(p, issued);
        }

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var accepted = approvals
            .Where(x => x.Accepted &&
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    x.ReviewedAtUtc, tz)) <= asOfDate).ToArray();
        decimal delta = 0m;
        foreach (var decision in accepted)
            delta = checked(delta + decision.ApprovedSignedDeltaAmount);
        var effective = checked(issued.TotalAmount + delta);
        if (effective <= 0m || effective < completedReceiptsAsOf)
            throw new InvalidDataException(
                "Adjusted monthly amount is below zero or already captured receipts.");
        return new MonthlyInvoiceAdjustmentSummary(
            issued.Id, issued.TotalAmount, delta, effective,
            accepted.Length, IsOfficialDebtPosted: false);
    }
}

public sealed record MonthlyInvoiceAdjustmentSummary(
    Guid IssuedInvoiceId, decimal OriginalAmount,
    decimal ApprovedSignedAdjustment, decimal AdjustedAmount,
    int ApprovedAdjustmentCount, bool IsOfficialDebtPosted);
