using System;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsD04PostingHandoffPolicy_Tests
{
    private const string BankReference = "BANK-PAYOS-D04-REF-1";
    private static readonly string EvidenceSha = new('b', 64);

    private static (PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox, Payment Payment)
        Reconciled()
    {
        var building = Guid.NewGuid();
        var payer = Guid.NewGuid();
        var payment = new Payment(Guid.NewGuid(), "D04-PAYOS-REVIEW", payer,
            PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, 150000m, contractId: Guid.NewGuid());
        var intent = new PayOsPaymentIntent(Guid.NewGuid(), building, payer,
            91550011, 150000, DateTime.UtcNow.AddHours(1), paymentId: payment.Id);
        intent.AttachProviderLink("D04-STAGING-LINK");
        intent.MarkConfirmed(BankReference, DateTime.UtcNow);
        var inbox = new PayOsWebhookInboxRecord(Guid.NewGuid(), building,
            intent.Id, intent.OrderCode, BankReference, new string('a', 64), DateTime.UtcNow);
        inbox.MarkReconciled(DateTime.UtcNow);
        return (intent, inbox, payment);
    }

    private static PayOsD04IndependentReview Verified(
        PayOsPaymentIntent intent, Payment payment)
        => new(
            BuildingId: intent.BuildingId,
            PaymentId: payment.Id,
            BankReviewerId: Guid.NewGuid(),
            ApprovingOwnerId: Guid.NewGuid(),
            ReviewerHasCurrentBankEvidenceGrant: true,
            OwnerHasCurrentPostingGrant: true,
            IndependentBankEvidenceVerified: true,
            IndependentlyVerifiedAmountVnd: intent.AmountVnd,
            IndependentlyVerifiedReference: BankReference,
            EvidenceSha256: EvidenceSha,
            VerifiedAtUtc: DateTime.UtcNow,
            CurrentContractScopeMatches: true,
            CurrentDepositCoverageMatches: true,
            LegacyPaymentLocked: true,
            BankReferenceUnusedInLegacyLedger: true);

    [Fact]
    public void Provider_reconciled_without_bank_proof_is_never_ready_for_D04_posting()
    {
        var (intent, inbox, payment) = Reconciled();
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentBankEvidence,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, null));
        var review = Verified(intent, payment) with { IndependentBankEvidenceVerified = false };
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentBankEvidence,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, review));
    }

    [Fact]
    public void Independent_bank_and_owner_review_only_make_a_candidate_for_locked_writer()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        Assert.Equal(PayOsD04HandoffDecision.ReadyForLockedD04Writer,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, review));
    }

    [Fact]
    public void Same_actor_or_revoked_owner_role_cannot_approve_money()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentApproval,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { ApprovingOwnerId = review.BankReviewerId }));
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentApproval,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { OwnerHasCurrentPostingGrant = false }));
    }

    [Fact]
    public void Different_bank_reference_or_amount_cannot_be_approved()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentBankEvidence,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { IndependentlyVerifiedReference = "OTHER-BANK-REF" }));
        Assert.Equal(PayOsD04HandoffDecision.HoldForIndependentBankEvidence,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { IndependentlyVerifiedAmountVnd = intent.AmountVnd - 1 }));
        Assert.Equal(PayOsD04HandoffDecision.ConflictRequiresInvestigation,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { BankReferenceUnusedInLegacyLedger = false }));
    }

    [Fact]
    public void Incomplete_legacy_lock_or_changed_contract_scope_blocks_handoff()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        Assert.Equal(PayOsD04HandoffDecision.ConflictRequiresInvestigation,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { LegacyPaymentLocked = false }));
        Assert.Equal(PayOsD04HandoffDecision.ConflictRequiresInvestigation,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment,
                review with { CurrentContractScopeMatches = false }));
    }

    [Fact]
    public void Completed_payment_without_exact_journal_is_conflict_not_replayed()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        payment.Complete(DateTime.UtcNow, BankReference);
        Assert.Equal(PayOsD04HandoffDecision.ConflictRequiresInvestigation,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, review));
        Assert.Equal(PayOsD04HandoffDecision.AlreadyPostedConsistently,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, payment, review,
                exactLegacyJournalAlreadyExists: true));
    }

    [Fact]
    public void Reconciled_inbox_and_approved_review_do_not_override_wrong_target()
    {
        var (intent, inbox, payment) = Reconciled();
        var review = Verified(intent, payment);
        var otherPayment = new Payment(payment.Id, "OTHER-D04-PAY", payment.TenantId,
            PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, payment.Amount + 1, contractId: payment.ContractId);
        Assert.Equal(PayOsD04HandoffDecision.ConflictRequiresInvestigation,
            PayOsD04PostingHandoffPolicy.Assess(intent, inbox, otherPayment, review));
    }
    [Fact]
    public void Crash_after_legacy_commit_requires_exact_journal_before_payos_ack()
    {
        var (intent, inbox, payment) = Reconciled();
        payment.Complete(DateTime.UtcNow, BankReference);
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04PostingHandoffPolicy.PlanAfterLegacyCommit(intent, inbox,
                payment, exactLegacyJournalAlreadyExists: false));
        Assert.Equal(PayOsD04RecoveryDecision.MarkPayOsPostedAfterLegacyProof,
            PayOsD04PostingHandoffPolicy.PlanAfterLegacyCommit(intent, inbox,
                payment, exactLegacyJournalAlreadyExists: true));
    }

    [Fact]
    public void Repeated_ack_after_already_posted_is_read_only()
    {
        var (intent, inbox, payment) = Reconciled();
        payment.Complete(DateTime.UtcNow, BankReference);
        intent.MarkPosted();
        Assert.Equal(PayOsD04RecoveryDecision.AlreadyAcknowledged,
            PayOsD04PostingHandoffPolicy.PlanAfterLegacyCommit(intent, inbox,
                payment, exactLegacyJournalAlreadyExists: true));
    }

    [Fact]
    public void Payos_marked_posted_without_completed_legacy_payment_is_inconsistent()
    {
        var (intent, inbox, payment) = Reconciled();
        intent.MarkPosted();
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04PostingHandoffPolicy.PlanAfterLegacyCommit(intent, inbox,
                payment, exactLegacyJournalAlreadyExists: true));
    }

    [Fact]
    public void Retry_and_crash_reuse_a_single_immutable_legacy_receipt_key()
    {
        var (intent, _, _) = Reconciled();
        var first = PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(intent);
        var retry = PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(intent);
        Assert.Equal(first, retry);
        Assert.StartsWith("PAYOS:D04:", first);

        var (other, _, _) = Reconciled();
        Assert.NotEqual(first, PayOsD04PostingHandoffPolicy.LegacyIdempotencyKey(other));
    }

}
