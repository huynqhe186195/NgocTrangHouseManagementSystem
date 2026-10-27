using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only, single-request dashboard decision for Phase 2G.3.
/// A clean preparation snapshot is NEVER proof of an issued invoice,
/// authorized financial terms, a posted receivable or refundable deposit.
/// </summary>
public sealed class FinalInvoiceReleaseReadiness
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public Guid? DraftInvoiceId { get; init; }
    public decimal? VerifiedBaseAmount { get; internal set; }
    public Guid? StagedEvidenceId { get; internal set; }
    public Guid? ReviewTicketId { get; internal set; }
    public bool StagedEvidenceMatchesCurrentDraft { get; internal set; }
    public bool IndependentDesignReviewRecorded { get; internal set; }
    // Historical signoff alone is insufficient. This flag requires the
    // source Draft still match the archived evidence AND no new preflight
    // anomaly since the design review. It never grants issuance.
    public bool IndependentReviewApplicableToCurrentDraft { get; internal set; }
    public List<string> Blockers { get; } = new();

    public bool CanIssue => false;
    public bool CanPostReceivable => false;
    public bool CanSettleDeposit => false;
}

public static class FinalInvoiceReleaseReadinessEvaluator
{
    public static FinalInvoiceReleaseReadiness Assess(
        FinalInvoiceIssuePreflight preflight,
        IReadOnlyCollection<FinalInvoiceIssueEvidenceRecord> stagedEvidence,
        IReadOnlyCollection<InvoiceIssuanceReviewTicket> reviewTickets,
        FinalInvoiceIssueEvidenceDriftReview? driftReview,
        InvoicePaymentAllocationJournalAudit allocationAudit)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(stagedEvidence);
        ArgumentNullException.ThrowIfNull(reviewTickets);
        ArgumentNullException.ThrowIfNull(allocationAudit);

        var result = new FinalInvoiceReleaseReadiness
        {
            MoveOutRequestId = preflight.MoveOutRequestId,
            ContractId = preflight.ContractId,
            DraftInvoiceId = preflight.UtilityDraftId,
            VerifiedBaseAmount = preflight.KnownBaseChargeTotal
        };

        foreach (var blocker in preflight.Blockers)
            result.Blockers.Add("Preflight:" + blocker);

        if (stagedEvidence.Count != 1 ||
            stagedEvidence.Any(x => x is null))
        {
            result.Blockers.Add(stagedEvidence.Count == 0
                ? "StagedInvoiceEvidenceMissing"
                : "StagedInvoiceEvidenceAmbiguous");
        }
        else
        {
            var archived = stagedEvidence.Single();
            try
            {
                archived.VerifyStoredEvidence();
                if (archived.MoveOutRequestId != preflight.MoveOutRequestId ||
                    archived.ContractId != preflight.ContractId ||
                    archived.RoomId != preflight.RoomId ||
                    archived.DraftInvoiceId != preflight.UtilityDraftId ||
                    archived.KnownBaseChargeTotal != preflight.KnownBaseChargeTotal)
                    result.Blockers.Add("StagedInvoiceEvidenceScopeMismatch");
                else
                    result.StagedEvidenceId = archived.Id;
            }
            catch (InvalidDataException)
            {
                result.Blockers.Add("StagedInvoiceEvidenceCorrupt");
            }

            if (result.StagedEvidenceId.HasValue)
            {
                if (driftReview is null ||
                    driftReview.EvidenceRecordId != archived.Id ||
                    driftReview.DraftInvoiceId != archived.DraftInvoiceId ||
                    driftReview.ContractId != archived.ContractId ||
                    !driftReview.MatchesCurrentDraft)
                    result.Blockers.Add("StagedInvoiceEvidenceNoLongerCurrent");
                else
                    result.StagedEvidenceMatchesCurrentDraft = true;
            }

            if (reviewTickets.Count != 1 ||
                reviewTickets.Any(x => x is null))
                result.Blockers.Add(reviewTickets.Count == 0
                    ? "IndependentInvoiceReviewMissing"
                    : "IndependentInvoiceReviewAmbiguous");
            else
            {
                var ticket = reviewTickets.Single();
                try
                {
                    ticket.VerifyStagedEvidence(archived);
                    if (result.StagedEvidenceId != archived.Id ||
                        ticket.StagedEvidenceId != archived.Id ||
                        ticket.MoveOutRequestId != preflight.MoveOutRequestId ||
                        ticket.Status != InvoiceIssuanceReviewStatus.ReviewedForDesign)
                        result.Blockers.Add("IndependentInvoiceReviewNotReady");
                    else
                    {
                        result.ReviewTicketId = ticket.Id;
                        result.IndependentDesignReviewRecorded = true;
                        // The ticket can remain historically true after
                        // a meter edit, reassignment or new draft-linked
                        // payment. Distinguish a historical review from
                        // evidence that is still applicable NOW.
                        if (!result.StagedEvidenceMatchesCurrentDraft)
                            result.Blockers.Add(
                                "IndependentInvoiceReviewSupersededByDraftChange");
                        else if (preflight.Blockers.Any(x =>
                            x is not "AuxiliaryFeePolicyUndecided" and
                                not "InvoiceDueDatePolicyUndecided" and
                                not "IssuerBuildingAuthorizationUnavailable" and
                                not "ImmutableIssuedInvoiceLedgerNotImplemented" and
                                not "AuditedPaymentAllocationNotImplemented"))
                            result.Blockers.Add(
                                "IndependentInvoiceReviewSupersededByNewAnomalies");
                        else if (allocationAudit.Blockers.Any(x =>
                            x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing"))
                            result.Blockers.Add(
                                "IndependentInvoiceReviewSupersededByAllocationAnomalies");
                        else
                            result.IndependentReviewApplicableToCurrentDraft = true;
                    }
                }
                catch (InvalidDataException)
                {
                    result.Blockers.Add("IndependentInvoiceReviewCorrupt");
                }
            }
        }

        if (stagedEvidence.Count == 0 && reviewTickets.Count != 0)
            result.Blockers.Add("InvoiceReviewWithoutEvidence");

        // This is not a financial settlement. A previously completed
        // payment linked to a draft remains an unresolved anomaly, not a
        // recognized debt reduction.
        foreach (var blocker in allocationAudit.Blockers)
            result.Blockers.Add("Allocations:" + blocker);

        // The SRS does not define signed auxiliary fees, due-date policy,
        // or authority to issue. Do not infer defaults from stored drafts.
        var financialTerms = FinalInvoiceFinancialTermsEvaluator.Assess(
            preflight, proposal: null);
        foreach (var blocker in financialTerms.Blockers)
            result.Blockers.Add("FinancialTerms:" + blocker);

        return result;
    }
}
