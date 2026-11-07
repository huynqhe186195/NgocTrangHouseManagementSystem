using System;

namespace BuildingManagement.Payments;

/// <summary>
/// Internal-only D04 commit contract between an independently authorized
/// reviewer, a FUTURE Building-DB transactional writer and the payOS ACK loop.
/// No implementation here writes a Payment, receipt, settlement or outbox.
/// Never bind these objects from a request or a payOS webhook.
///
/// A production caller MUST fetch review/approval from durable independent
/// audit storage and revalidate Identity grants and building scope *inside*
/// the legacy transaction. ReceiptOutboxProof MUST be built by a trusted
/// database reader under the canonical Building-DB source journal.
/// </summary>
public static class PayOsD04LegacyCommitProtocol
{
    public static PayOsD04LegacyCommitPlan CreateCandidate(
        PayOsPaymentIntent intent, PayOsWebhookInboxRecord inbox,
        Payment payment, PayOsD04IndependentReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, review) !=
            PayOsD04HandoffDecision.ReadyForLockedD04Writer)
            throw new InvalidOperationException(
                "D04 approval is not ready. Never post or infer a receipt from payOS alone.");

        return new PayOsD04LegacyCommitPlan(
            intent.Id, intent.BuildingId, payment.Id, intent.OrderCode,
            intent.AmountVnd, intent.ProviderReference!,
            PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(intent),
            review.EvidenceSha256!, review.BankReviewerId, review.ApprovingOwnerId);
    }

    /// <summary>
    /// Pure proof-shape validation and recovery classification. Returns NO
    /// permission to create a financial journal. The caller must obtain the
    /// proof from an authoritative Building DB reader, not the webhook/API.
    /// </summary>
    public static PayOsD04RecoveryDecision ClassifyCommittedReceipt(
        PayOsPaymentIntent intent, PayOsWebhookInboxRecord inbox,
        Payment payment, PayOsD04LegacyCommitPlan plan,
        PayOsD04ReceiptOutboxProof? proof)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(plan);

        if (proof is null ||
            plan.IntentId != intent.Id || plan.BuildingId != intent.BuildingId ||
            plan.PaymentId != payment.Id || plan.OrderCode != intent.OrderCode ||
            plan.AmountVnd != intent.AmountVnd ||
            plan.BankReviewerId == plan.OwnerApproverId ||
            plan.BankReviewerId == Guid.Empty ||
            plan.OwnerApproverId == Guid.Empty ||
            plan.SourceKey != PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(intent) ||
            !string.Equals(plan.BankReference, intent.ProviderReference,
                StringComparison.Ordinal) ||
            proof.IntentId != plan.IntentId ||
            proof.BuildingId != plan.BuildingId ||
            proof.PaymentId != plan.PaymentId ||
            proof.SourceKey != plan.SourceKey ||
            proof.ReceiptId == Guid.Empty || proof.DepositSettlementId == Guid.Empty ||
            proof.OutboxId == Guid.Empty ||
            !string.Equals(proof.BankReference, plan.BankReference,
                StringComparison.Ordinal) ||
            proof.AmountVnd != plan.AmountVnd ||
            proof.SettlementAmountVnd != plan.AmountVnd ||
            proof.ApprovedByOwnerId != plan.OwnerApproverId ||
            !string.Equals(proof.EvidenceSha256, plan.EvidenceSha256,
                StringComparison.Ordinal) ||
            proof.ExactSourceReceiptCount != 1 ||
            proof.ExactSettlementCount != 1 ||
            proof.ExactOutboxCount != 1 ||
            proof.GlobalBankReferenceCount != 1 ||
            proof.ReceiptCreatedAtUtc.Kind != DateTimeKind.Utc ||
            proof.OutboxCreatedAtUtc.Kind != DateTimeKind.Utc ||
            proof.OutboxCreatedAtUtc < proof.ReceiptCreatedAtUtc)
            return PayOsD04RecoveryDecision.StopAndInvestigate;

        // A source-proven journal bundle is required in ADDITION to the
        // currently completed Payment and the original signed inbox.
        return PayOsD04PostingHandoffPolicy.PlanAfterLegacyCommit(
            intent, inbox, payment, exactLegacyJournalAlreadyExists: true);
    }
}

/// <summary>
/// Candidate immutable intent for the *future* locked legacy D04 writer.
/// This is NOT a durable Owner approval. Never make a public request DTO.
/// </summary>
public sealed record PayOsD04LegacyCommitPlan(
    Guid IntentId,
    Guid BuildingId,
    Guid PaymentId,
    long OrderCode,
    long AmountVnd,
    string BankReference,
    string SourceKey,
    string EvidenceSha256,
    Guid BankReviewerId,
    Guid OwnerApproverId);

/// <summary>
/// Shape of an authoritative, already committed legacy payment/journal/outbox
/// read-back. This is only meaningful when populated from the Building DB.
/// No caller-provided count, receipt ID or digest can prove money was posted.
/// </summary>
public sealed record PayOsD04ReceiptOutboxProof(
    Guid IntentId,
    Guid BuildingId,
    Guid PaymentId,
    Guid ReceiptId,
    Guid DepositSettlementId,
    Guid OutboxId,
    string SourceKey,
    string BankReference,
    long AmountVnd,
    long SettlementAmountVnd,
    string EvidenceSha256,
    Guid ApprovedByOwnerId,
    int ExactSourceReceiptCount,
    int ExactSettlementCount,
    int ExactOutboxCount,
    int GlobalBankReferenceCount,
    DateTime ReceiptCreatedAtUtc,
    DateTime OutboxCreatedAtUtc);
