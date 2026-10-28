using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Durable snapshot of an EXPLICIT root decision for the final contract
/// deposit. Never invents a debt-offset policy: ALL issued invoices must
/// be evidenced as fully attributed before a refund or forfeiture plan.
/// A manual bank proof reference is a signed operator attestation, NOT a
/// verification against a bank account or payment processor.
/// </summary>
public sealed class ContractDepositCloseoutApprovalRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid IssuedInvoiceId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ApprovedByUserId { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }
    public decimal VerifiedHeldAmount { get; private set; }
    public decimal ApprovedForfeitAmount { get; private set; }
    public decimal ApprovedRefundAmount { get; private set; }
    public Guid? RefundSettlementId { get; private set; }
    public Guid? ForfeitSettlementId { get; private set; }
    public string? RefundTransactionReference { get; private set; }
    public DateTime? RefundPaidAtUtc { get; private set; }
    public string? DamageReason { get; private set; }
    public string? DamageEvidenceReference { get; private set; }
    public string InspectionEvidenceReference { get; private set; } = string.Empty;
    public string HandoverEvidenceReference { get; private set; } = string.Empty;
    public string ExplicitDepositPolicyReference { get; private set; } = string.Empty;
    public string ApprovalJson { get; private set; } = string.Empty;
    public string ApprovalSha256 { get; private set; } = string.Empty;

    private ContractDepositCloseoutApprovalRecord() { }
    private ContractDepositCloseoutApprovalRecord(Guid id) : base(id) { }

    public bool IsAutomaticDebtOffset => false;
    public bool IsVerifiedBankTransfer => false;

    /// <summary>
    /// Candidate from locked, persisted inputs only. A future writer must
    /// also hold root Identity + building DB membership until COMMIT and
    /// persist actual Refund/Forfeit settlements in SAME transaction.
    /// </summary>
    public static ContractDepositCloseoutApprovalRecord ApproveCandidate(
        Guid id, Guid rootActorId, DateTime approvedAtUtc,
        MoveOutRequest request, Contract contract,
        IssuedInvoiceLedgerRecord finalInvoice,
        IReadOnlyCollection<IssuedInvoiceLedgerRecord> allIssuedInvoices,
        IReadOnlyCollection<InvoicePaymentAllocationJournalEntry> allocations,
        InvoicePaymentAllocationJournalAudit paymentAudit,
        ContractDepositLedgerReview depositAudit,
        decimal explicitlyApprovedForfeitAmount,
        string? damageReason, string? damageEvidenceReference,
        string inspectionEvidenceReference, string handoverEvidenceReference,
        string explicitDepositPolicyReference,
        Guid? refundSettlementId, Guid? forfeitSettlementId,
        string? refundTransactionReference, DateTime? refundPaidAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(finalInvoice);
        ArgumentNullException.ThrowIfNull(allIssuedInvoices);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(paymentAudit);
        ArgumentNullException.ThrowIfNull(depositAudit);

        if (id == Guid.Empty || rootActorId == Guid.Empty ||
            approvedAtUtc == default || approvedAtUtc.Kind != DateTimeKind.Utc ||
            request.Status != MoveOutRequestStatus.Approved ||
            contract.Status != ContractStatus.Active ||
            request.ContractId != contract.Id ||
            request.RoomId != contract.RoomId ||
            finalInvoice.ContractId != contract.Id ||
            finalInvoice.RoomId != contract.RoomId ||
            finalInvoice.MoveOutRequestId != request.Id ||
            finalInvoice.BuildingId == Guid.Empty ||
            finalInvoice.IssuedAtUtc > approvedAtUtc)
            throw new InvalidOperationException(
                "Deposit closeout requires an approved move-out, locked active contract and issued invoice.");

        // The old read-only auditors deliberately retain expected
        // NO-AUTHORITY blocker codes; any other anomaly is a HARD stop.
        if (depositAudit.MoveOutRequestId != request.Id ||
            depositAudit.ContractId != contract.Id ||
            depositAudit.VerifiedIncomingAllocationTotal is not decimal held ||
            depositAudit.RecordedCompletedOutgoingTotal != 0m ||
            held <= 0m ||
            depositAudit.Blockers.Any(x => x is not
                "FinalInvoiceDebtNotReconciled" and
                not "DepositDeductionPolicyNotApproved" and
                not "RefundAuthorizationAndHandoverMissing"))
            throw new InvalidOperationException(
                "No complete verified held deposit ledger is available.");

        if (paymentAudit.Blockers.Any(x =>
                x != "IssuedInvoiceLedgerAndAllocationAuthorizationMissing") ||
            !paymentAudit.VerifiedStagedAmount.HasValue ||
            allIssuedInvoices.Count == 0 ||
            allIssuedInvoices.Any(x =>
                x is null || x.ContractId != contract.Id ||
                x.TotalAmount <= 0m) ||
            allocations.Any(x => x is null ||
                x.ContractId != contract.Id ||
                x.Amount <= 0m) ||
            allIssuedInvoices.Select(x => x.Id).Distinct().Count() !=
                allIssuedInvoices.Count ||
            allocations.Select(x => x.PaymentId).Distinct().Count() !=
                allocations.Count ||
            !allIssuedInvoices.Any(x => x.Id == finalInvoice.Id) ||
            allocations.Any(x => allIssuedInvoices.All(i =>
                i.Id != x.IssuedInvoiceId)) ||
            allIssuedInvoices.Any(i =>
                allocations.Where(a => a.IssuedInvoiceId == i.Id)
                    .Sum(a => a.Amount) != i.TotalAmount) ||
            paymentAudit.VerifiedStagedAmount.Value !=
                allocations.Sum(a => a.Amount))
            throw new InvalidOperationException(
                "ALL issued contract invoices require individually audited completed receipts. No automatic deposit debt offset.");

        if (decimal.Truncate(held) != held ||
            explicitlyApprovedForfeitAmount < 0m ||
            decimal.Truncate(explicitlyApprovedForfeitAmount) !=
                explicitlyApprovedForfeitAmount ||
            explicitlyApprovedForfeitAmount > held)
            throw new InvalidOperationException(
                "Explicit forfeiture must be a whole-VND amount within verified held funds.");

        var refund = held - explicitlyApprovedForfeitAmount;
        if (refundedAtBeforeApproval(refundPaidAtUtc, finalInvoice.IssuedAtUtc) ||
            !HasReference(inspectionEvidenceReference, 8, 500) ||
            !HasReference(handoverEvidenceReference, 8, 500) ||
            !HasReference(explicitDepositPolicyReference, 8, 200) ||
            (explicitlyApprovedForfeitAmount > 0m &&
                (forfeitSettlementId.GetValueOrDefault() == Guid.Empty ||
                 !HasReference(damageReason, 8, 500) ||
                 !HasReference(damageEvidenceReference, 8, 500))) ||
            (explicitlyApprovedForfeitAmount == 0m &&
                (forfeitSettlementId.HasValue ||
                 !string.IsNullOrWhiteSpace(damageReason) ||
                 !string.IsNullOrWhiteSpace(damageEvidenceReference))) ||
            (refund > 0m &&
                (refundSettlementId.GetValueOrDefault() == Guid.Empty ||
                 !HasReference(refundTransactionReference, 8, 128) ||
                 !refundPaidAtUtc.HasValue ||
                 refundPaidAtUtc.Value.Kind != DateTimeKind.Utc)) ||
            (refund == 0m &&
                (refundSettlementId.HasValue ||
                 !string.IsNullOrWhiteSpace(refundTransactionReference) ||
                 refundPaidAtUtc.HasValue)) ||
            (refundSettlementId.HasValue &&
             refundSettlementId == forfeitSettlementId))
            throw new InvalidOperationException(
                "Signed inspection/handover, explicit damage decision and verified manual refund proof are mandatory.");

        var proof = JsonSerializer.Serialize(new
        {
            ContractId = contract.Id,
            MoveOutRequestId = request.Id,
            IssuedInvoiceId = finalInvoice.Id,
            BuildingId = finalInvoice.BuildingId,
            ApprovedByUserId = rootActorId,
            ApprovedAtUtc = approvedAtUtc,
            VerifiedHeldAmount = held,
            ApprovedForfeitAmount = explicitlyApprovedForfeitAmount,
            ApprovedRefundAmount = refund,
            RefundSettlementId = refundSettlementId,
            ForfeitSettlementId = forfeitSettlementId,
            RefundTransactionReference = refundTransactionReference?.Trim(),
            RefundPaidAtUtc = refundPaidAtUtc,
            DamageReason = damageReason?.Trim(),
            DamageEvidenceReference = damageEvidenceReference?.Trim(),
            InspectionEvidenceReference = inspectionEvidenceReference.Trim(),
            HandoverEvidenceReference = handoverEvidenceReference.Trim(),
            ExplicitDepositPolicyReference = explicitDepositPolicyReference.Trim()
        });
        if (proof.Length > 1000000)
            throw new InvalidOperationException("Approval evidence exceeds archival limit.");

        var result = new ContractDepositCloseoutApprovalRecord(id)
        {
            ContractId = contract.Id,
            MoveOutRequestId = request.Id,
            IssuedInvoiceId = finalInvoice.Id,
            BuildingId = finalInvoice.BuildingId,
            ApprovedByUserId = rootActorId,
            ApprovedAtUtc = approvedAtUtc,
            VerifiedHeldAmount = held,
            ApprovedForfeitAmount = explicitlyApprovedForfeitAmount,
            ApprovedRefundAmount = refund,
            RefundSettlementId = refundSettlementId,
            ForfeitSettlementId = forfeitSettlementId,
            RefundTransactionReference = refundTransactionReference?.Trim(),
            RefundPaidAtUtc = refundPaidAtUtc,
            DamageReason = damageReason?.Trim(),
            DamageEvidenceReference = damageEvidenceReference?.Trim(),
            InspectionEvidenceReference = inspectionEvidenceReference.Trim(),
            HandoverEvidenceReference = handoverEvidenceReference.Trim(),
            ExplicitDepositPolicyReference = explicitDepositPolicyReference.Trim(),
            ApprovalJson = proof,
            ApprovalSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(proof))).ToLowerInvariant()
        };
        result.VerifyStoredApproval();
        return result;
    }

    public void VerifyStoredApproval()
    {
        if (Id == Guid.Empty || ContractId == Guid.Empty ||
            MoveOutRequestId == Guid.Empty || IssuedInvoiceId == Guid.Empty ||
            BuildingId == Guid.Empty || ApprovedByUserId == Guid.Empty ||
            ApprovedAtUtc == default || VerifiedHeldAmount <= 0m ||
            ApprovedRefundAmount < 0m || ApprovedForfeitAmount < 0m ||
            ApprovedRefundAmount + ApprovedForfeitAmount != VerifiedHeldAmount ||
            !HasReference(InspectionEvidenceReference, 8, 500) ||
            !HasReference(HandoverEvidenceReference, 8, 500) ||
            !HasReference(ExplicitDepositPolicyReference, 8, 200) ||
            string.IsNullOrWhiteSpace(ApprovalJson) ||
            !StringComparer.Ordinal.Equals(ApprovalSha256,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(ApprovalJson))).ToLowerInvariant()))
            throw new InvalidDataException("Deposit approval archive is inconsistent.");

        try
        {
            using var document = JsonDocument.Parse(ApprovalJson);
            var root = document.RootElement;
            if (root.GetProperty("ContractId").GetGuid() != ContractId ||
                root.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                root.GetProperty("IssuedInvoiceId").GetGuid() != IssuedInvoiceId ||
                root.GetProperty("BuildingId").GetGuid() != BuildingId ||
                root.GetProperty("ApprovedByUserId").GetGuid() != ApprovedByUserId ||
                root.GetProperty("VerifiedHeldAmount").GetDecimal() != VerifiedHeldAmount ||
                root.GetProperty("ApprovedForfeitAmount").GetDecimal() != ApprovedForfeitAmount ||
                root.GetProperty("ApprovedRefundAmount").GetDecimal() != ApprovedRefundAmount)
                throw new InvalidDataException("Deposit closeout approval JSON does not match indexed fields.");
        }
        catch (Exception ex) when (ex is JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Deposit approval JSON is invalid.", ex);
        }
    }

    private static bool HasReference(string? value, int min, int max)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Trim().Length >= min &&
           value.Trim().Length <= max;

    private static bool refundedAtBeforeApproval(
        DateTime? paidAtUtc, DateTime invoiceIssuedAtUtc)
        => paidAtUtc.HasValue && (paidAtUtc.Value == default ||
            paidAtUtc.Value < invoiceIssuedAtUtc);
}
