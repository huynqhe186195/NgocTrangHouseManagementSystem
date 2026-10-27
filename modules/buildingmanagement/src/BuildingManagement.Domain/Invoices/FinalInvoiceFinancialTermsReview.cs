using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Invoices;

/// <summary>
/// A PROPOSED set of financial terms for a future final invoice.
/// Must be resolved/approved by a real authenticated, building-scoped
/// workflow, not trusted from an external DTO. Neither this proposal nor
/// its audit outcome can issue a bill or recognize debt.
/// </summary>
public sealed record FinalInvoiceFinancialTermsProposal(
    Guid DraftInvoiceId,
    Guid ContractId,
    Guid RoomId,
    string EvidenceSha256,
    DateOnly? PaymentDueDate,
    string? DueDatePolicyReference,
    AuxiliaryFeeAssessmentDecision AuxiliaryFeeDecision,
    string? AuxiliaryFeePolicyReference,
    Guid? AuxiliaryFeeReviewerId,
    IReadOnlyCollection<FinalInvoiceAuxiliaryChargeProposal>? AuxiliaryCharges);

public enum AuxiliaryFeeAssessmentDecision
{
    NotReviewed = 0,
    ExplicitlyNoCharges = 1,
    ItemizedCharges = 2
}

public enum FinalInvoiceAuxiliaryFeeKind
{
    Internet = 1,
    Trash = 2,
    Parking = 3,
    OtherContractedService = 4
}

/// <summary>
/// Contract/rate references are evidence references, not an authorization.
/// Damage or deposit deduction is NOT a fee kind: those need an independent
/// itemized dispute/settlement workflow to prevent double collection.
/// </summary>
public sealed record FinalInvoiceAuxiliaryChargeProposal(
    FinalInvoiceAuxiliaryFeeKind Kind,
    string ServiceCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    string ContractOrRateReference,
    string Description);

public sealed class FinalInvoiceFinancialTermsReview
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public Guid? DraftInvoiceId { get; init; }
    public decimal? VerifiedBaseAmount { get; internal set; }
    public decimal? ProposedAuxiliaryAmount { get; internal set; }
    public decimal? ProposedGrandTotal { get; internal set; }
    public DateOnly? DocumentedDueDate { get; internal set; }
    public List<string> Blockers { get; } = new();
    public bool CanIssue => false;
    public bool IsReceivablePosted => false;
    public bool CanSettleDeposit => false;
}

public static class FinalInvoiceFinancialTermsEvaluator
{
    public static FinalInvoiceFinancialTermsReview Assess(
        FinalInvoiceIssuePreflight preflight,
        FinalInvoiceFinancialTermsProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        var review = new FinalInvoiceFinancialTermsReview
        {
            MoveOutRequestId = preflight.MoveOutRequestId,
            ContractId = preflight.ContractId,
            DraftInvoiceId = preflight.UtilityDraftId
        };

        if (preflight.UtilityDraftId is null ||
            preflight.KnownBaseChargeTotal is not decimal baseAmount ||
            baseAmount <= 0m ||
            preflight.BaseCharges.Count != 3 ||
            preflight.BaseCharges.Sum(x => x.Amount) != baseAmount ||
            string.IsNullOrWhiteSpace(preflight.EvidenceSha256) ||
            preflight.Blockers.Any(x => x.StartsWith("Bill:", StringComparison.Ordinal) ||
                x.StartsWith("Payment:", StringComparison.Ordinal) ||
                x is "PaymentReviewScopeMismatch" or
                    "VerifiedFinalInvoiceEvidenceMissing" or
                    "VerifiedEvidenceMismatch"))
            review.Blockers.Add("VerifiedFinalInvoiceBaseEvidenceRequired");
        else
            review.VerifiedBaseAmount = baseAmount;

        if (proposal is null)
        {
            review.Blockers.Add("AuxiliaryFeeAssessmentNotProvided");
            review.Blockers.Add("InvoiceDueDatePolicyNotProvided");
            return Stop(review);
        }

        if (proposal.DraftInvoiceId != preflight.UtilityDraftId ||
            proposal.ContractId != preflight.ContractId ||
            proposal.RoomId != preflight.RoomId ||
            string.IsNullOrWhiteSpace(proposal.EvidenceSha256) ||
            !StringComparer.Ordinal.Equals(
                proposal.EvidenceSha256, preflight.EvidenceSha256))
            review.Blockers.Add("ProposedTermsDoNotMatchInvoiceEvidence");

        if (proposal.PaymentDueDate is null ||
            proposal.PaymentDueDate == default ||
            string.IsNullOrWhiteSpace(proposal.DueDatePolicyReference))
            review.Blockers.Add("InvoiceDueDatePolicyNotProvided");
        else
            review.DocumentedDueDate = proposal.PaymentDueDate;

        if (proposal.AuxiliaryFeeDecision is not
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges and
                not AuxiliaryFeeAssessmentDecision.ItemizedCharges ||
            string.IsNullOrWhiteSpace(proposal.AuxiliaryFeePolicyReference) ||
            !proposal.AuxiliaryFeeReviewerId.HasValue ||
            proposal.AuxiliaryFeeReviewerId == Guid.Empty ||
            proposal.AuxiliaryCharges is null)
        {
            review.Blockers.Add("AuxiliaryFeeAssessmentNotProvided");
            return Stop(review);
        }

        var charges = proposal.AuxiliaryCharges.ToArray();
        if (proposal.AuxiliaryFeeDecision ==
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges)
        {
            if (charges.Length != 0)
                review.Blockers.Add("ExplicitNoFeeDecisionHasChargeLines");
            else
                review.ProposedAuxiliaryAmount = 0m;
        }
        else if (charges.Length == 0)
        {
            review.Blockers.Add("ItemizedAuxiliaryFeesMissing");
        }
        else
        {
            if (charges.Any(x => x is null ||
                !Enum.IsDefined(x.Kind) ||
                string.IsNullOrWhiteSpace(x.ServiceCode) ||
                x.ServiceCode.Trim().Length > 64 ||
                string.IsNullOrWhiteSpace(x.Description) ||
                x.Description.Trim().Length > 500 ||
                string.IsNullOrWhiteSpace(x.ContractOrRateReference) ||
                x.ContractOrRateReference.Trim().Length > 200 ||
                x.Quantity <= 0m || x.UnitPrice <= 0m ||
                x.Amount <= 0m || x.Quantity * x.UnitPrice != x.Amount ||
                decimal.Truncate(x.Amount) != x.Amount))
                review.Blockers.Add("AuxiliaryChargeMissingValidPricingEvidence");

            if (charges.GroupBy(x =>
                    (x.Kind, x.ServiceCode?.Trim().ToUpperInvariant()))
                .Any(g => g.Count() > 1))
                review.Blockers.Add("DuplicateAuxiliaryServiceCharge");

            if (review.Blockers.Count == 0 ||
                review.Blockers.All(x =>
                    x is "VerifiedFinalInvoiceBaseEvidenceRequired" or
                        "InvoiceDueDatePolicyNotProvided" or
                        "ProposedTermsDoNotMatchInvoiceEvidence"))
                review.ProposedAuxiliaryAmount = charges.Sum(x => x.Amount);
        }

        return Stop(review);
    }

    private static FinalInvoiceFinancialTermsReview Stop(
        FinalInvoiceFinancialTermsReview review)
    {
        if (review.Blockers.Count == 0 &&
            review.VerifiedBaseAmount.HasValue &&
            review.ProposedAuxiliaryAmount.HasValue)
            review.ProposedGrandTotal =
                review.VerifiedBaseAmount.Value + review.ProposedAuxiliaryAmount.Value;

        // This is a domain input assessment only. Identity rights,
        // fee approval, immutable issued ledger and audited payment
        // allocations remain mandatory future transaction checks.
        review.Blockers.Add("FinancialTermsProposalIsNotPostingAuthorization");
        return review;
    }
}
