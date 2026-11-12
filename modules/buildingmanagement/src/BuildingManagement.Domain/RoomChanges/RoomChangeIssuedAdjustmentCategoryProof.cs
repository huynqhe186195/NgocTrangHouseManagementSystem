using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// D03 line classification of an independently ACCEPTED monthly invoice
/// adjustment. Each signed credit/debit note must be reconciled to the
/// original rent/electricity/water/service lines without rewriting issuance.
/// This is NOT a posted adjustment to the official receivable.
/// </summary>
public sealed record RoomChangeIssuedAdjustmentCategoryLine(
    ContractDepositOffsetCategory Category,
    decimal SignedDeltaVnd,
    string SignedCategoryEvidenceReference);

public sealed class RoomChangeIssuedAdjustmentCategoryProof
{
    public Guid Id { get; private init; }
    public Guid IssuedInvoiceId { get; private init; }
    public Guid ContractId { get; private init; }
    public Guid BuildingId { get; private init; }
    public Guid AdjustmentId { get; private init; }
    public Guid DecisionId { get; private init; }
    public string OriginalInvoiceSha256 { get; private init; } = string.Empty;
    public decimal ApprovedSignedDeltaVnd { get; private init; }
    public Guid ClassifiedByUserId { get; private init; }
    public DateTime ClassifiedAtUtc { get; private init; }
    public string SignedClassificationReference { get; private init; } = string.Empty;
    public IReadOnlyList<RoomChangeIssuedAdjustmentCategoryLine> Lines { get; private init; }
        = Array.Empty<RoomChangeIssuedAdjustmentCategoryLine>();
    public string EvidenceSha256 { get; private init; } = string.Empty;
    private RoomChangeIssuedAdjustmentCategoryProof() { }

    public static RoomChangeIssuedAdjustmentCategoryProof Capture(
        Guid id, MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceAdjustmentRecord proposal,
        MonthlyInvoiceAdjustmentDecisionRecord decision,
        IReadOnlyCollection<RoomChangeIssuedAdjustmentCategoryLine> lines,
        Guid classifier, DateTime classifiedAtUtc, string? signedReference)
    {
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(lines);
        proposal.VerifyAgainst(issued);
        decision.VerifyAgainst(proposal, issued);

        var reference = signedReference?.Trim() ?? string.Empty;
        var ordered = lines.OrderBy(x => x.Category).ToArray();
        if (id == Guid.Empty || !decision.Accepted ||
            classifier == Guid.Empty ||
            classifier == proposal.ProposedByUserId ||
            classifier == decision.ReviewedByUserId ||
            classifiedAtUtc.Kind != DateTimeKind.Utc ||
            classifiedAtUtc < decision.ReviewedAtUtc ||
            reference.Length is < 12 or > 500 ||
            ordered.Length is < 1 or > 4 ||
            ordered.Any(x =>
                x is null ||
                x.Category is not (ContractDepositOffsetCategory.UnpaidRent or
                    ContractDepositOffsetCategory.Electricity or
                    ContractDepositOffsetCategory.Water or
                    ContractDepositOffsetCategory.ApprovedService) ||
                x.SignedDeltaVnd == 0m ||
                decimal.Truncate(x.SignedDeltaVnd) != x.SignedDeltaVnd ||
                Math.Sign(x.SignedDeltaVnd) !=
                    Math.Sign(decision.ApprovedSignedDeltaAmount) ||
                string.IsNullOrWhiteSpace(x.SignedCategoryEvidenceReference) ||
                x.SignedCategoryEvidenceReference.Trim().Length is < 12 or > 500) ||
            ordered.Select(x => x.Category).Distinct().Count() != ordered.Length)
            throw new InvalidOperationException(
                "Approved invoice correction needs complete, independently signed category evidence.");
        decimal total;
        try
        {
            total = ordered.Aggregate(0m,
                (sum, x) => checked(sum + x.SignedDeltaVnd));
        }
        catch (OverflowException e)
        {
            throw new InvalidOperationException("Signed correction overflows whole VND.", e);
        }
        if (total != decision.ApprovedSignedDeltaAmount)
            throw new InvalidOperationException(
                "Signed per-category correction must sum exactly to original approved invoice delta.");

        var candidate = new RoomChangeIssuedAdjustmentCategoryProof
        {
            Id = id, IssuedInvoiceId = issued.Id,
            ContractId = issued.ContractId, BuildingId = issued.BuildingId,
            AdjustmentId = proposal.Id, DecisionId = decision.Id,
            OriginalInvoiceSha256 = issued.CandidateSha256,
            ApprovedSignedDeltaVnd = total,
            ClassifiedByUserId = classifier,
            ClassifiedAtUtc = Normalize(classifiedAtUtc),
            SignedClassificationReference = reference,
            Lines = Array.AsReadOnly(ordered.Select(x =>
                x with { SignedCategoryEvidenceReference =
                    x.SignedCategoryEvidenceReference.Trim() }).ToArray())
        };
        return new RoomChangeIssuedAdjustmentCategoryProof
        {
            Id = candidate.Id, IssuedInvoiceId = candidate.IssuedInvoiceId,
            ContractId = candidate.ContractId, BuildingId = candidate.BuildingId,
            AdjustmentId = candidate.AdjustmentId, DecisionId = candidate.DecisionId,
            OriginalInvoiceSha256 = candidate.OriginalInvoiceSha256,
            ApprovedSignedDeltaVnd = candidate.ApprovedSignedDeltaVnd,
            ClassifiedByUserId = candidate.ClassifiedByUserId,
            ClassifiedAtUtc = candidate.ClassifiedAtUtc,
            SignedClassificationReference = candidate.SignedClassificationReference,
            Lines = candidate.Lines,
            EvidenceSha256 = candidate.Hash()
        };
    }

    public void Verify(
        MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceAdjustmentRecord proposal,
        MonthlyInvoiceAdjustmentDecisionRecord decision)
    {
        if (Id == Guid.Empty || OriginalInvoiceSha256 != issued.CandidateSha256 ||
            IssuedInvoiceId != issued.Id ||
            ContractId != issued.ContractId || BuildingId != issued.BuildingId ||
            AdjustmentId != proposal.Id || DecisionId != decision.Id ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("D03 signed category correction is detached from source invoice.");
        try
        {
            var rebuilt = Capture(
                Id, issued, proposal, decision, Lines,
                ClassifiedByUserId, ClassifiedAtUtc,
                SignedClassificationReference);
            if (rebuilt.EvidenceSha256 != EvidenceSha256)
                throw new InvalidDataException("D03 signed correction categories drifted.");
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("Original issued correction evidence inconsistent.", e);
        }
    }

    private string Hash()
    {
        var parts = new List<string>
        {
            Id.ToString("D"), IssuedInvoiceId.ToString("D"),
            ContractId.ToString("D"), BuildingId.ToString("D"),
            AdjustmentId.ToString("D"), DecisionId.ToString("D"),
            OriginalInvoiceSha256,
            ApprovedSignedDeltaVnd.ToString("0", CultureInfo.InvariantCulture),
            ClassifiedByUserId.ToString("D"),
            ClassifiedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            SignedClassificationReference
        };
        foreach (var line in Lines)
        {
            parts.Add(((int)line.Category).ToString(CultureInfo.InvariantCulture));
            parts.Add(line.SignedDeltaVnd.ToString("0", CultureInfo.InvariantCulture));
            parts.Add(line.SignedCategoryEvidenceReference);
        }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", parts)))).ToLowerInvariant();
    }

    private static DateTime Normalize(DateTime instant)
        => instant.AddTicks(-(instant.Ticks % 10));
    public bool IsOfficialDebtAdjustmentPosted => false;
    public bool CanReduceReceivable => false;
}

/// <summary>
/// Complete source-history audit of proposed AND decided corrections. The
/// absence of a decision or of a category proof for an accepted decision
/// fails closed. Rejected decisions affect no category. No credit is
/// applied to an official posting; classification remains pre-posting.
/// </summary>
public static class RoomChangeIssuedAdjustmentCategoryEvaluator
{
    public static RoomChangeIssuedAdjustmentCategorySummary Assess(
        MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceCandidateLedgerRecord frozen,
        MonthlyInvoiceIndependentReviewRecord issuanceReview,
        DateOnly asOfDate,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord> proposals,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord> decisions,
        IReadOnlyCollection<RoomChangeIssuedAdjustmentCategoryProof> classifications)
    {
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(issuanceReview);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(classifications);
        if (asOfDate == default)
            throw new InvalidOperationException("D03 date required.");

        issued.VerifyAgainst(frozen, issuanceReview);
        frozen.VerifyStoredEvidence();
        if (frozen.ContractId != issued.ContractId ||
            frozen.BuildingId != issued.BuildingId ||
            frozen.RoomId != issued.RoomId ||
            frozen.FrozenEvidenceSha256 != issued.CandidateSha256)
            throw new InvalidDataException("D03 original frozen invoice differs.");

        var ps = proposals.ToArray();
        var ds = decisions.ToArray();
        var cs = classifications.ToArray();
        if (ps.Any(x => x is null) || ds.Any(x => x is null) ||
            cs.Any(x => x is null) ||
            ps.Select(x => x.Id).Distinct().Count() != ps.Length ||
            ds.Select(x => x.AdjustmentId).Distinct().Count() != ds.Length ||
            cs.Select(x => x.AdjustmentId).Distinct().Count() != cs.Length ||
            ps.Length != ds.Length)
            throw new InvalidDataException("D03 missing, duplicated or unreviewed correction history.");

        var decisionByProposal = ds.ToDictionary(x => x.AdjustmentId);
        var proofByProposal = cs.ToDictionary(x => x.AdjustmentId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        DateOnly Day(DateTime time) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(time, zone));

        var totalByCategory = new Dictionary<ContractDepositOffsetCategory, decimal>();
        foreach (var p in ps)
        {
            p.VerifyAgainst(issued);
            if (!decisionByProposal.TryGetValue(p.Id, out var decision))
                throw new InvalidDataException("D03 pending adjustment cannot enter authoritative rent.");
            decision.VerifyAgainst(p, issued);
            if (Day(decision.ReviewedAtUtc) > asOfDate)
                throw new InvalidDataException("D03 future decision is not effective.");
            var hasProof = proofByProposal.TryGetValue(p.Id, out var proof);
            if (!decision.Accepted)
            {
                if (hasProof)
                    throw new InvalidDataException("Rejected D03 invoice correction cannot be classified as accepted.");
                continue;
            }
            if (!hasProof || proof is null)
                throw new InvalidDataException("Accepted D03 adjustment requires signed category split.");
            proof.Verify(issued, p, decision);
            if (Day(proof.ClassifiedAtUtc) > asOfDate)
                throw new InvalidDataException("D03 future line split cannot affect current invoice.");
            foreach (var line in proof.Lines)
            {
                totalByCategory.TryGetValue(line.Category, out var old);
                totalByCategory[line.Category] = checked(old + line.SignedDeltaVnd);
            }
        }
        if (proofByProposal.Keys.Except(ps.Select(x => x.Id)).Any())
            throw new InvalidDataException("Orphaned D03 category correction.");

        decimal Get(ContractDepositOffsetCategory category)
            => totalByCategory.TryGetValue(category, out var value) ? value : 0m;
        var rent = Get(ContractDepositOffsetCategory.UnpaidRent);
        var electricity = Get(ContractDepositOffsetCategory.Electricity);
        var water = Get(ContractDepositOffsetCategory.Water);
        var service = Get(ContractDepositOffsetCategory.ApprovedService);
        if (frozen.RentAmount + rent < 0m ||
            frozen.ElectricityAmount + electricity < 0m ||
            frozen.WaterAmount + water < 0m ||
            frozen.AuxiliaryAmount + service < 0m ||
            frozen.TotalAmount + rent + electricity + water + service <= 0m)
            throw new InvalidDataException("D03 proposed correction makes an original invoice component negative.");
        return new RoomChangeIssuedAdjustmentCategorySummary(
            issued.Id, rent, electricity, water, service,
            cs.Length, IsOfficialPosting: false);
    }
}

public sealed record RoomChangeIssuedAdjustmentCategorySummary(
    Guid IssuedInvoiceId, decimal RentSignedDeltaVnd,
    decimal ElectricitySignedDeltaVnd, decimal WaterSignedDeltaVnd,
    decimal ServiceSignedDeltaVnd, int ClassifiedAdjustmentCount,
    bool IsOfficialPosting)
{
    public bool CanExecuteRoomChange => false;
    public bool CanApplyCreditToRealDebt => false;
}
