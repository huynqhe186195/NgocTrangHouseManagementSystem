using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Owner-approved OI-04 priority for a future authenticated deposit closeout.
/// This is a READ-ONLY calculation over already verified server-side facts.
/// It never marks bank receipts as verified, writes a settlement, authorizes a
/// refund or allows clients to certify debt/approval with Boolean switches.
/// Only an independently audited application/SQL writer may later consume
/// a versioned approval and execute a real allocation.
/// </summary>
public static class ContractDepositOffsetPreviewPolicy
{
    public static ContractDepositOffsetPreview Assess(
        decimal verifiedHeldVnd,
        Guid contractId,
        Guid ownerApprovalId,
        bool ownerApprovalVerified,
        string? signedPolicyReference,
        IReadOnlyCollection<ContractDepositOffsetClaim>? verifiedClaims,
        IReadOnlyCollection<ContractDepositOwnerTierAllocation>? ownerTierAllocations = null)
    {
        var blockers = new List<string>();
        if (contractId == Guid.Empty || ownerApprovalId == Guid.Empty ||
            !ownerApprovalVerified || !Reference(signedPolicyReference))
            blockers.Add("OwnerApprovalOrSignedPolicyMissing");

        if (verifiedHeldVnd <= 0m || !WholeVnd(verifiedHeldVnd))
            blockers.Add("VerifiedHeldDepositInvalid");

        if (verifiedClaims is null)
        {
            blockers.Add("DebtEvidenceNotLoaded");
            return Denied(verifiedHeldVnd, blockers);
        }

        var claims = verifiedClaims.ToArray();
        if (claims.Any(x => x is null))
        {
            blockers.Add("DebtClaimMissing");
            return Denied(verifiedHeldVnd, blockers);
        }

        if (claims.Select(x => x.ClaimId).Distinct().Count() != claims.Length)
            blockers.Add("DuplicateDebtClaim");

        foreach (var claim in claims)
        {
            if (claim.ClaimId == Guid.Empty || !Enum.IsDefined(claim.Category) ||
                claim.OutstandingVnd <= 0m || !WholeVnd(claim.OutstandingVnd))
            {
                blockers.Add("DebtClaimInvalid");
                continue;
            }

            // Neither an open invoice nor a client-entered damage sum is a
            // confirmed offsetable obligation. In dispute, fail the entire
            // preview rather than silently reallocating funds elsewhere.
            if (!claim.IndependentlyVerified || claim.IsDisputed ||
                !Reference(claim.SignedEvidenceReference))
                blockers.Add("UnverifiedOrDisputedDebt");

            if (claim.Category == ContractDepositOffsetCategory.ApprovedDamage)
            {
                if (!Reference(claim.DamageReason) ||
                    !Reference(claim.DamageEvidenceReference))
                    blockers.Add("OwnerApprovedDamageEvidenceMissing");
            }
            else if (!string.IsNullOrWhiteSpace(claim.DamageReason) ||
                     !string.IsNullOrWhiteSpace(claim.DamageEvidenceReference))
            {
                blockers.Add("DamageEvidenceOnNonDamageDebt");
            }
        }

        if (blockers.Count != 0)
            return Denied(verifiedHeldVnd, blockers);

        // Same rank for electricity/water: do not invent an owner-approved
        // priority between the two. Sorting by stable claim ID is only a
        // deterministic presentation tie-breaker, not a chronological
        // allocation instruction or a completed transfer of money.
        var ordered = claims
            .OrderBy(x => Priority(x.Category))
            .ThenBy(x => x.ClaimId)
            .ToArray();
        var ownerSplit = ownerTierAllocations?.ToArray()
            ?? Array.Empty<ContractDepositOwnerTierAllocation>();
        if (ownerSplit.Any(x => x.ClaimId == Guid.Empty ||
                x.ApprovedAppliedVnd < 0m ||
                !WholeVnd(x.ApprovedAppliedVnd)) ||
            ownerSplit.Select(x => x.ClaimId).Distinct().Count() != ownerSplit.Length)
            return Denied(verifiedHeldVnd,
                new[] { "InvalidOwnerSignedTierSplit" });
        var splitByClaim = ownerSplit.ToDictionary(x => x.ClaimId);
        int? splitPriority = null;
        // The owner approved rent -> (electricity and water) -> service
        // -> damage, but NOT precedence among multiple claims at the same
        // rank. If insufficient cash would split one rank, never select a
        // winner by GUID/order: that would invent a financial policy.
        var available = verifiedHeldVnd;
        foreach (var tier in ordered.GroupBy(x => Priority(x.Category)))
        {
            var required = tier.Sum(x => x.OutstandingVnd);
            if (available > 0m && available < required && tier.Count() > 1)
            {
                var items = tier.ToArray();
                if (ownerSplit.Length != items.Length ||
                    items.Any(x => !splitByClaim.TryGetValue(x.ClaimId, out var split) ||
                        split.ApprovedAppliedVnd > x.OutstandingVnd) ||
                    ownerSplit.Sum(x => x.ApprovedAppliedVnd) != available)
                    return Denied(verifiedHeldVnd,
                        new[] { "PartialPriorityTierRequiresAllocationRule" });
                splitPriority = tier.Key;
                break;
            }
            available = Math.Max(0m, available - required);
        }

        if (splitPriority is null && ownerSplit.Length > 0)
            return Denied(verifiedHeldVnd,
                new[] { "UnexpectedOwnerTierSplit" });

        var remaining = verifiedHeldVnd;
        var allocations = new List<ContractDepositOffsetPreviewLine>(ordered.Length);
        decimal unpaid = 0m;
        foreach (var claim in ordered)
        {
            var applied = splitPriority == Priority(claim.Category)
                ? splitByClaim[claim.ClaimId].ApprovedAppliedVnd
                : Math.Min(remaining, claim.OutstandingVnd);
            remaining -= applied;
            var stillDue = claim.OutstandingVnd - applied;
            unpaid += stillDue;
            allocations.Add(new ContractDepositOffsetPreviewLine(
                claim.ClaimId, claim.Category, claim.OutstandingVnd,
                applied, stillDue));
        }

        return new ContractDepositOffsetPreview(
            verifiedHeldVnd, remaining, unpaid, allocations.AsReadOnly(),
            Array.Empty<string>());
    }

    private static ContractDepositOffsetPreview Denied(
        decimal held, IReadOnlyCollection<string> blockers)
        => new(held, null, null,
            Array.Empty<ContractDepositOffsetPreviewLine>(),
            blockers.Distinct(StringComparer.Ordinal).ToArray());

    private static int Priority(ContractDepositOffsetCategory category) =>
        category switch
        {
            ContractDepositOffsetCategory.UnpaidRent => 1,
            ContractDepositOffsetCategory.Electricity => 2,
            ContractDepositOffsetCategory.Water => 2,
            ContractDepositOffsetCategory.ApprovedService => 3,
            ContractDepositOffsetCategory.ApprovedDamage => 4,
            _ => int.MaxValue
        };

    private static bool WholeVnd(decimal amount) =>
        decimal.Truncate(amount) == amount;

    private static bool Reference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 8 and <= 500;
}

public enum ContractDepositOffsetCategory
{
    UnpaidRent = 1,
    Electricity = 2,
    Water = 3,
    ApprovedService = 4,
    ApprovedDamage = 5
}

/// <summary>
/// This input must be composed from *persisted*, independently reviewed
/// receivables and damage approvals, never from HTTP form data.
/// </summary>
public sealed record ContractDepositOffsetClaim(
    Guid ClaimId,
    ContractDepositOffsetCategory Category,
    decimal OutstandingVnd,
    bool IndependentlyVerified,
    bool IsDisputed,
    string? SignedEvidenceReference,
    string? DamageReason = null,
    string? DamageEvidenceReference = null);

/// <summary>
    /// Exact Owner-signed per-claim VND allocation ONLY for an underfunded
    /// equal-priority tier (e.g. electricity versus water). This cannot
    /// modify verified original debt or re-order higher-priority charges.
    /// </summary>
public sealed record ContractDepositOwnerTierAllocation(Guid ClaimId,
    decimal ApprovedAppliedVnd);

public sealed record ContractDepositOffsetPreviewLine(
    Guid ClaimId,
    ContractDepositOffsetCategory Category,
    decimal OriginalOutstandingVnd,
    decimal ProposedAppliedVnd,
    decimal RemainingDebtVnd);

/// <summary>
/// Proposed amounts are NOT bank settlement evidence. This type has no
/// Executed, RefundAuthorized or BankTransferVerified property by design.
/// </summary>
public sealed record ContractDepositOffsetPreview(
    decimal ObservedHeldVnd,
    decimal? ProposedRefundableVnd,
    decimal? ProposedUncoveredDebtVnd,
    IReadOnlyList<ContractDepositOffsetPreviewLine> ProposedAllocations,
    IReadOnlyList<string> Blockers)
{
    public bool IsEvidenceCompleteForPreview => Blockers.Count == 0;
    public bool CanExecuteRefund => false;
    public bool CanPostDebtOffset => false;
}
