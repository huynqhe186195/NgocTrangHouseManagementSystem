using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// OI-04 owner-authorized, immutable PLAN of how verified held deposit may
/// cover independently reviewed official debt. Never posts a debt payment,
/// releases cash, closes a tenancy or creates a refund. An executing writer
/// must re-read every source under a building/Identity commit lease, then
/// match this exact snapshot in the SAME serializable DB transaction.
/// </summary>
public sealed class ContractDepositDebtOffsetApprovalRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ApprovedByOwnerId { get; private set; }
    public Guid IndependentDebtReviewerId { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }
    public decimal VerifiedHeldAmount { get; private set; }
    public decimal ApprovedOffsetAmount { get; private set; }
    public decimal ExpectedRemainingDeposit { get; private set; }
    public decimal ExpectedUncoveredDebt { get; private set; }
    public string SignedPolicyReference { get; private set; } = string.Empty;
    public string IndependentReviewReference { get; private set; } = string.Empty;
    public string ApprovalJson { get; private set; } = string.Empty;
    public string ApprovalSha256 { get; private set; } = string.Empty;

    public bool IsOffsetPosted => false;
    public bool IsBankRefundCompleted => false;

    private ContractDepositDebtOffsetApprovalRecord() { }
    private ContractDepositDebtOffsetApprovalRecord(Guid id) : base(id) { }

    public static ContractDepositDebtOffsetApprovalRecord Approve(
        Guid id, Guid contractId, Guid moveOutRequestId, Guid buildingId,
        Guid ownerId, Guid independentDebtReviewerId, DateTime approvedAtUtc,
        decimal verifiedHeld, string? policyReference,
        string? independentReviewReference,
        IReadOnlyCollection<ContractDepositOffsetClaim>? verifiedClaims,
        IReadOnlyCollection<ContractDepositOwnerTierAllocation>? ownerTierAllocations = null)
    {
        if (id == Guid.Empty || contractId == Guid.Empty ||
            moveOutRequestId == Guid.Empty || buildingId == Guid.Empty ||
            ownerId == Guid.Empty || independentDebtReviewerId == Guid.Empty ||
            ownerId == independentDebtReviewerId ||
            approvedAtUtc.Kind != DateTimeKind.Utc ||
            !Reference(policyReference) || !Reference(independentReviewReference) ||
            verifiedClaims is null || verifiedClaims.Count is < 1 or > 250)
            throw new InvalidOperationException(
                "An independent reviewed debt set and authenticated Owner approval are required.");

        var snapshot = verifiedClaims.ToArray();
        var ownerSplit = ownerTierAllocations?.OrderBy(x => x.ClaimId).ToArray()
            ?? Array.Empty<ContractDepositOwnerTierAllocation>();
        var review = ContractDepositOffsetPreviewPolicy.Assess(
            verifiedHeld, contractId, id, true, policyReference, snapshot, ownerSplit);
        if (!review.IsEvidenceCompleteForPreview ||
            review.ProposedAllocations.Count != snapshot.Length ||
            review.ProposedRefundableVnd is not decimal heldAfter ||
            review.ProposedUncoveredDebtVnd is not decimal dueAfter)
            throw new InvalidOperationException(
                "Disputed, incomplete or ambiguous priority allocations cannot be approved.");

        var approved = review.ProposedAllocations.Sum(x => x.ProposedAppliedVnd);
        if (approved <= 0m || approved > verifiedHeld ||
            heldAfter != verifiedHeld - approved ||
            snapshot.Sum(x => x.OutstandingVnd) != approved + dueAfter)
            throw new InvalidOperationException(
                "Owner may approve only a nonempty balanced verified debt offset.");

        var now = approvedAtUtc.AddTicks(-(approvedAtUtc.Ticks % 10L));
        // Canonical order makes the exact claim set and priority reproducible
        // after serialization, regardless of a caller's input order.
        var canonical = snapshot.OrderBy(x => x.ClaimId).ToArray();
        var proof = JsonSerializer.Serialize(new
        {
            ContractId = contractId, MoveOutRequestId = moveOutRequestId,
            BuildingId = buildingId, ApprovedByOwnerId = ownerId,
            IndependentDebtReviewerId = independentDebtReviewerId,
            ApprovedAtUtc = now, VerifiedHeldAmount = verifiedHeld,
            ApprovedOffsetAmount = approved,
            ExpectedRemainingDeposit = heldAfter,
            ExpectedUncoveredDebt = dueAfter,
            SignedPolicyReference = policyReference!.Trim(),
            IndependentReviewReference = independentReviewReference!.Trim(),
            Claims = canonical,
            OwnerTierAllocations = ownerSplit,
            ApprovedLines = review.ProposedAllocations
        });
        if (proof.Length is < 100 or > 1000000)
            throw new InvalidOperationException("Invalid signed offset proof.");

        var row = new ContractDepositDebtOffsetApprovalRecord(id)
        {
            ContractId = contractId, MoveOutRequestId = moveOutRequestId,
            BuildingId = buildingId, ApprovedByOwnerId = ownerId,
            IndependentDebtReviewerId = independentDebtReviewerId,
            ApprovedAtUtc = now, VerifiedHeldAmount = verifiedHeld,
            ApprovedOffsetAmount = approved,
            ExpectedRemainingDeposit = heldAfter,
            ExpectedUncoveredDebt = dueAfter,
            SignedPolicyReference = policyReference.Trim(),
            IndependentReviewReference = independentReviewReference.Trim(),
            ApprovalJson = proof,
            ApprovalSha256 = Sha(proof)
        };
        row.VerifyStoredApproval();
        return row;
    }

    public void VerifyStoredApproval()
    {
        if (Id == Guid.Empty || ContractId == Guid.Empty ||
            MoveOutRequestId == Guid.Empty || BuildingId == Guid.Empty ||
            ApprovedByOwnerId == Guid.Empty ||
            IndependentDebtReviewerId == Guid.Empty ||
            ApprovedByOwnerId == IndependentDebtReviewerId ||
            ApprovedAtUtc.Kind != DateTimeKind.Utc ||
            VerifiedHeldAmount <= 0m || !Whole(VerifiedHeldAmount) ||
            ApprovedOffsetAmount <= 0m || !Whole(ApprovedOffsetAmount) ||
            ApprovedOffsetAmount > VerifiedHeldAmount ||
            ExpectedRemainingDeposit != VerifiedHeldAmount - ApprovedOffsetAmount ||
            ExpectedUncoveredDebt < 0m || !Whole(ExpectedUncoveredDebt) ||
            !Reference(SignedPolicyReference) ||
            !Reference(IndependentReviewReference) ||
            ApprovalJson.Length is < 100 or > 1000000 ||
            !StringComparer.Ordinal.Equals(ApprovalSha256, Sha(ApprovalJson)))
            throw new InvalidDataException("Owner offset approval ledger is inconsistent.");

        try
        {
            using var doc = JsonDocument.Parse(ApprovalJson);
            var root = doc.RootElement;
            if (root.GetProperty("ContractId").GetGuid() != ContractId ||
                root.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                root.GetProperty("BuildingId").GetGuid() != BuildingId ||
                root.GetProperty("ApprovedByOwnerId").GetGuid() != ApprovedByOwnerId ||
                root.GetProperty("IndependentDebtReviewerId").GetGuid() !=
                    IndependentDebtReviewerId ||
                root.GetProperty("ApprovedAtUtc").GetDateTime() != ApprovedAtUtc ||
                root.GetProperty("VerifiedHeldAmount").GetDecimal() != VerifiedHeldAmount ||
                root.GetProperty("ApprovedOffsetAmount").GetDecimal() != ApprovedOffsetAmount ||
                root.GetProperty("ExpectedRemainingDeposit").GetDecimal() !=
                    ExpectedRemainingDeposit ||
                root.GetProperty("ExpectedUncoveredDebt").GetDecimal() !=
                    ExpectedUncoveredDebt ||
                root.GetProperty("SignedPolicyReference").GetString() !=
                    SignedPolicyReference ||
                root.GetProperty("IndependentReviewReference").GetString() !=
                    IndependentReviewReference)
                throw new InvalidDataException("Owner offset snapshot differs from indexed values.");

            var claims = JsonSerializer.Deserialize<ContractDepositOffsetClaim[]>(
                root.GetProperty("Claims").GetRawText()) ??
                throw new InvalidDataException("Missing signed claim list.");
            var ownerSplit = root.TryGetProperty("OwnerTierAllocations", out var splits)
                ? JsonSerializer.Deserialize<ContractDepositOwnerTierAllocation[]>(
                    splits.GetRawText()) ?? throw new InvalidDataException(
                        "Invalid signed owner tier allocation.")
                : Array.Empty<ContractDepositOwnerTierAllocation>();
            var expected = ContractDepositOffsetPreviewPolicy.Assess(
                VerifiedHeldAmount, ContractId, Id, true,
                SignedPolicyReference, claims, ownerSplit);
            if (!expected.IsEvidenceCompleteForPreview ||
                expected.ProposedRefundableVnd != ExpectedRemainingDeposit ||
                expected.ProposedUncoveredDebtVnd != ExpectedUncoveredDebt ||
                expected.ProposedAllocations.Sum(x => x.ProposedAppliedVnd) !=
                    ApprovedOffsetAmount)
                throw new InvalidDataException("Owner offset allocation calculation drifted.");
            var serializedLines =
                root.GetProperty("ApprovedLines").GetRawText();
            var frozenLines =
                JsonSerializer.Deserialize<ContractDepositOffsetPreviewLine[]>(
                    serializedLines) ?? throw new InvalidDataException(
                        "Missing signed allocation breakdown.");
            if (!expected.ProposedAllocations.SequenceEqual(frozenLines))
                throw new InvalidDataException("Owner offset allocation order or money changed.");
        }
        catch (Exception ex) when (ex is JsonException or
            KeyNotFoundException or ArgumentException or
            FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException(
                "Owner debt offset approval JSON is invalid or unbalanced.", ex);
        }
    }

    public IReadOnlyList<ContractDepositOwnerTierAllocation> ReadOwnerTierAllocations()
    {
        VerifyStoredApproval();
        using var doc = JsonDocument.Parse(ApprovalJson);
        if (!doc.RootElement.TryGetProperty("OwnerTierAllocations", out var split))
            return Array.Empty<ContractDepositOwnerTierAllocation>();
        return Array.AsReadOnly(
            JsonSerializer.Deserialize<ContractDepositOwnerTierAllocation[]>(
                split.GetRawText()) ?? Array.Empty<ContractDepositOwnerTierAllocation>());
    }

    public void RequireCurrentEvidence(
        Guid contract, Guid request, Guid building,
        decimal currentlyHeld,
        IReadOnlyCollection<ContractDepositOffsetClaim> currentClaims)
    {
        VerifyStoredApproval();
        if (ContractId != contract || MoveOutRequestId != request ||
            BuildingId != building || currentlyHeld != VerifiedHeldAmount)
            throw new InvalidOperationException("Held funds or scope changed after Owner approval.");
        using var doc = JsonDocument.Parse(ApprovalJson);
        var frozen = JsonSerializer.Deserialize<ContractDepositOffsetClaim[]>(
            doc.RootElement.GetProperty("Claims").GetRawText())!;
        if (currentClaims.Count != frozen.Length ||
            !currentClaims.OrderBy(x => x.ClaimId)
                .SequenceEqual(frozen.OrderBy(x => x.ClaimId)))
            throw new InvalidOperationException("Receivables or dispute evidence changed after approval.");
    }

    private static bool Reference(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        s.Trim().Length is >= 12 and <= 500;
    private static bool Whole(decimal x) => decimal.Truncate(x) == x;
    private static string Sha(string s) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
