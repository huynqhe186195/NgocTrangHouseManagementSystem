using System;

namespace BuildingManagement.Payments;

/// <summary>
/// Pure, fail-closed D04 handoff gate. It NEVER completes Payment, creates a
/// deposit settlement, posts a receipt, or changes an intent/inbox row.
///
/// The caller MUST populate review evidence only from independently verified
/// bank documents, authenticated Identity grants and a locked legacy D04
/// transaction. No browser/webhook-controlled boolean may reach this policy.
/// A "Ready" result is NOT authorization to post without the missing durable,
/// idempotent, cross-database financial writer.
/// </summary>
public static class PayOsD04PostingHandoffPolicy
{
    public static PayOsD04HandoffDecision Assess(
        PayOsPaymentIntent intent,
        PayOsWebhookInboxRecord inbox,
        Payment payment,
        PayOsD04IndependentReview? review,
        bool exactLegacyJournalAlreadyExists = false)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(payment);

        if (intent.Id == Guid.Empty || intent.PaymentId != payment.Id ||
            intent.InvoiceId.HasValue ||
            intent.BuildingId == Guid.Empty || intent.PayerId != payment.TenantId ||
            payment.Purpose != PaymentPurpose.ContractDeposit ||
            payment.Method != PaymentMethod.BankTransfer ||
            payment.Channel != PaymentChannel.VietQr ||
            !payment.ContractId.HasValue || payment.ContractId == Guid.Empty ||
            payment.Amount <= 0m || decimal.Truncate(payment.Amount) != payment.Amount ||
            payment.Amount != intent.AmountVnd ||
            string.IsNullOrWhiteSpace(intent.PaymentLinkId) ||
            inbox.PaymentIntentId != intent.Id ||
            inbox.BuildingId != intent.BuildingId ||
            inbox.OrderCode != intent.OrderCode ||
            !string.Equals(inbox.ProviderReference, intent.ProviderReference,
                StringComparison.Ordinal))
            return PayOsD04HandoffDecision.ConflictRequiresInvestigation;

        if (payment.Status == PaymentStatus.Completed)
        {
            // Recovery after crash between legacy posting and payOS marker.
            // A matching bank reference alone is insufficient: the legacy D04
            // writer must prove the EXACT completed Payment + settlement/
            // receipt journal under its own transaction before this result.
            return exactLegacyJournalAlreadyExists &&
                intent.Status is PayOsPaymentIntentStatus.ConfirmedPendingPosting or
                    PayOsPaymentIntentStatus.Posted &&
                inbox.Status == PayOsWebhookInboxStatus.ReconciledNoPosting &&
                string.Equals(payment.TransactionReference, intent.ProviderReference,
                    StringComparison.Ordinal)
                ? PayOsD04HandoffDecision.AlreadyPostedConsistently
                : PayOsD04HandoffDecision.ConflictRequiresInvestigation;
        }

        if (payment.Status != PaymentStatus.Pending ||
            intent.Status == PayOsPaymentIntentStatus.Posted ||
            (!string.IsNullOrWhiteSpace(payment.TransactionReference) &&
             !string.Equals(payment.TransactionReference, intent.ProviderReference,
                StringComparison.Ordinal)))
            return PayOsD04HandoffDecision.ConflictRequiresInvestigation;

        if (intent.Status != PayOsPaymentIntentStatus.ConfirmedPendingPosting ||
            inbox.Status != PayOsWebhookInboxStatus.ReconciledNoPosting)
            return PayOsD04HandoffDecision.HoldUntilReconciled;

        if (review is null || !review.IndependentBankEvidenceVerified ||
            review.IndependentlyVerifiedAmountVnd != intent.AmountVnd ||
            !string.Equals(review.IndependentlyVerifiedReference,
                intent.ProviderReference, StringComparison.Ordinal) ||
            review.EvidenceSha256 is null || !IsLowerSha256(review.EvidenceSha256) ||
            review.VerifiedAtUtc.Kind != DateTimeKind.Utc)
            return PayOsD04HandoffDecision.HoldForIndependentBankEvidence;

        if (review.BuildingId != intent.BuildingId ||
            review.PaymentId != payment.Id || !review.CurrentContractScopeMatches ||
            !review.CurrentDepositCoverageMatches || !review.LegacyPaymentLocked ||
            !review.BankReferenceUnusedInLegacyLedger)
            return PayOsD04HandoffDecision.ConflictRequiresInvestigation;

        if (review.BankReviewerId == Guid.Empty ||
            review.ApprovingOwnerId == Guid.Empty ||
            review.BankReviewerId == review.ApprovingOwnerId ||
            !review.ReviewerHasCurrentBankEvidenceGrant ||
            !review.OwnerHasCurrentPostingGrant)
            return PayOsD04HandoffDecision.HoldForIndependentApproval;

        // This is only an eligibility snapshot, NOT a durable approval or a
        // receipt. The future D04 writer must atomically re-check every proof
        // and insert a unique source receipt/outbox key in Building DB.
        return PayOsD04HandoffDecision.ReadyForLockedD04Writer;
    }

    private static bool IsLowerSha256(string s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }
}

/// <summary>
/// INTERNAL review snapshot only; never bind this type from a public DTO.
/// Flags must be generated by trusted database/Identity/evidence readers.
/// No bank integration is claimed by this type.
/// </summary>
public sealed record PayOsD04IndependentReview(
    Guid BuildingId,
    Guid PaymentId,
    Guid BankReviewerId,
    Guid ApprovingOwnerId,
    bool ReviewerHasCurrentBankEvidenceGrant,
    bool OwnerHasCurrentPostingGrant,
    bool IndependentBankEvidenceVerified,
    long IndependentlyVerifiedAmountVnd,
    string? IndependentlyVerifiedReference,
    string? EvidenceSha256,
    DateTime VerifiedAtUtc,
    bool CurrentContractScopeMatches,
    bool CurrentDepositCoverageMatches,
    bool LegacyPaymentLocked,
    bool BankReferenceUnusedInLegacyLedger);

public enum PayOsD04HandoffDecision
{
    HoldUntilReconciled = 0,
    HoldForIndependentBankEvidence = 1,
    HoldForIndependentApproval = 2,
    ConflictRequiresInvestigation = 3,
    ReadyForLockedD04Writer = 4,
    AlreadyPostedConsistently = 5
}
