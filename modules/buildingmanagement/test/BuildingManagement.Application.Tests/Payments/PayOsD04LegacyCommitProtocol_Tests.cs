using System;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsD04LegacyCommitProtocol_Tests
{
    [Fact]
    public void D04_pure_policy_must_live_in_domain_not_application()
    {
        Assert.Same(typeof(Payment).Assembly,
            typeof(PayOsD04PostingHandoffPolicy).Assembly);
        Assert.Same(typeof(Payment).Assembly,
            typeof(PayOsD04LegacyCommitProtocol).Assembly);
    }

    private static readonly string Ref = "BANK-D04-INDEPENDENT-TEST";
    private static readonly string Digest = new('c', 64);

    private static (PayOsPaymentIntent Intent, PayOsWebhookInboxRecord Inbox,
        Payment Payment, PayOsD04IndependentReview Review) Reconciled()
    {
        var building = Guid.NewGuid();
        var payer = Guid.NewGuid();
        var payment = new Payment(Guid.NewGuid(), "D04-RECOVERY-TEST", payer,
            PaymentPurpose.ContractDeposit, PaymentMethod.BankTransfer,
            PaymentChannel.VietQr, 150000m, contractId: Guid.NewGuid());
        var intent = new PayOsPaymentIntent(Guid.NewGuid(), building, payer,
            91551001, 150000, DateTime.UtcNow.AddDays(1), paymentId: payment.Id);
        intent.AttachProviderLink("D04-STAGING-LINK");
        intent.MarkConfirmed(Ref, DateTime.UtcNow);
        var inbox = new PayOsWebhookInboxRecord(Guid.NewGuid(), building,
            intent.Id, intent.OrderCode, Ref, new string('a', 64), DateTime.UtcNow);
        inbox.MarkReconciled(DateTime.UtcNow);
        var review = new PayOsD04IndependentReview(
            BuildingId: building,
            PaymentId: payment.Id,
            BankReviewerId: Guid.NewGuid(),
            ApprovingOwnerId: Guid.NewGuid(),
            ReviewerHasCurrentBankEvidenceGrant: true,
            OwnerHasCurrentPostingGrant: true,
            IndependentBankEvidenceVerified: true,
            IndependentlyVerifiedAmountVnd: intent.AmountVnd,
            IndependentlyVerifiedReference: Ref,
            EvidenceSha256: Digest,
            VerifiedAtUtc: DateTime.UtcNow,
            CurrentContractScopeMatches: true,
            CurrentDepositCoverageMatches: true,
            LegacyPaymentLocked: true,
            BankReferenceUnusedInLegacyLedger: true);
        return (intent, inbox, payment, review);
    }

    private static PayOsD04ReceiptOutboxProof Proof(PayOsD04LegacyCommitPlan plan)
    {
        var at = DateTime.UtcNow;
        return new PayOsD04ReceiptOutboxProof(
            IntentId: plan.IntentId, BuildingId: plan.BuildingId,
            PaymentId: plan.PaymentId, ReceiptId: Guid.NewGuid(),
            DepositSettlementId: Guid.NewGuid(), OutboxId: Guid.NewGuid(),
            SourceKey: plan.SourceKey, BankReference: plan.BankReference,
            AmountVnd: plan.AmountVnd, SettlementAmountVnd: plan.AmountVnd,
            EvidenceSha256: plan.EvidenceSha256,
            ApprovedByOwnerId: plan.OwnerApproverId,
            ExactSourceReceiptCount: 1, ExactSettlementCount: 1,
            ExactOutboxCount: 1, GlobalBankReferenceCount: 1,
            ReceiptCreatedAtUtc: at, OutboxCreatedAtUtc: at);
    }

    [Fact]
    public void Signed_provider_confirmation_alone_cannot_create_legacy_candidate()
    {
        var (intent, inbox, payment, review) = Reconciled();
        Assert.Throws<InvalidOperationException>(() =>
            PayOsD04LegacyCommitProtocol.CreateCandidate(
                intent, inbox, payment,
                review with { IndependentBankEvidenceVerified = false }));
    }

    [Fact]
    public void Independent_evidence_produces_stable_unique_source_candidate()
    {
        var (intent, inbox, payment, review) = Reconciled();
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review);
        Assert.Equal(intent.Id, plan.IntentId);
        Assert.Equal(payment.Id, plan.PaymentId);
        Assert.Equal(150000, plan.AmountVnd);
        Assert.Equal(Ref, plan.BankReference);
        Assert.Equal("PAYOS:D04:" + intent.Id.ToString("N"), plan.SourceKey);
        Assert.Equal(review.ApprovingOwnerId, plan.OwnerApproverId);
        Assert.Equal(plan, PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review));
    }

    [Fact]
    public void Before_commit_or_missing_legacy_proof_never_acknowledges_payos()
    {
        var (intent, inbox, payment, review) = Reconciled();
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review);
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan, null));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan, Proof(plan)));
    }

    [Fact]
    public void Post_commit_before_ack_requires_exact_receipt_settlement_and_outbox()
    {
        var (intent, inbox, payment, review) = Reconciled();
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review);
        payment.Complete(DateTime.UtcNow, Ref);
        Assert.Equal(PayOsD04RecoveryDecision.MarkPayOsPostedAfterLegacyProof,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan, Proof(plan)));

        // Repeated delivery must become an ACK-only operation.
        intent.MarkPosted();
        Assert.Equal(PayOsD04RecoveryDecision.AlreadyAcknowledged,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan, Proof(plan)));
    }

    [Fact]
    public void Mismatched_evidence_actor_and_source_never_acknowledge()
    {
        var (intent, inbox, payment, review) = Reconciled();
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review);
        payment.Complete(DateTime.UtcNow, Ref);
        var proof = Proof(plan);
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { EvidenceSha256 = new string('a', 64) }));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { ApprovedByOwnerId = review.BankReviewerId }));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { SourceKey = "PAYOS:D04:FORGED" }));
    }

    [Fact]
    public void Incomplete_journal_duplicate_bank_reference_or_bad_clock_stops_recovery()
    {
        var (intent, inbox, payment, review) = Reconciled();
        var plan = PayOsD04LegacyCommitProtocol.CreateCandidate(
            intent, inbox, payment, review);
        payment.Complete(DateTime.UtcNow, Ref);
        var proof = Proof(plan);
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { ExactOutboxCount = 0 }));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { ExactSettlementCount = 2 }));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { GlobalBankReferenceCount = 2 }));
        Assert.Equal(PayOsD04RecoveryDecision.StopAndInvestigate,
            PayOsD04LegacyCommitProtocol.ClassifyCommittedReceipt(
                intent, inbox, payment, plan,
                proof with { OutboxCreatedAtUtc = DateTime.UtcNow.AddDays(-1) }));
    }
}
