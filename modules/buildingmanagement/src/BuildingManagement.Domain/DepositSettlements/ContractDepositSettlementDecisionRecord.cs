using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Phase 2G.4: independent, immutable decision on the HELD contract deposit.
/// This is a plan, not a bank refund or a completed DepositSettlement.
/// Debt offset is deliberately unsupported until an explicit business policy
/// is approved. Actual Refund/Forfeit records remain in the existing ledger
/// and are completed only by the Phase 2G.5 atomic closeout.
/// </summary>
public sealed class ContractDepositSettlementDecisionRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid FinalIssuedInvoiceId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid ApprovedByUserId { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }
    public decimal VerifiedHeldAmount { get; private set; }
    public decimal ApprovedForfeitAmount { get; private set; }
    public decimal ApprovedRefundAmount { get; private set; }
    public decimal ApprovedDebtOffsetAmount { get; private set; }
    public string? DamageReason { get; private set; }
    public string? DamageEvidenceReference { get; private set; }
    public string ExplicitDepositPolicyReference { get; private set; } = string.Empty;
    public string ApprovalJson { get; private set; } = string.Empty;
    public string ApprovalSha256 { get; private set; } = string.Empty;

    public bool IsRefundExecuted => false;
    public bool IsDepositTransferExecuted => false;
    public bool IsAutomaticDebtOffset => false;

    private ContractDepositSettlementDecisionRecord() { }
    private ContractDepositSettlementDecisionRecord(Guid id) : base(id) { }

    public static ContractDepositSettlementDecisionRecord Approve(
        Guid id, Guid contractId, Guid moveOutRequestId,
        Guid finalIssuedInvoiceId, Guid buildingId, Guid actorId,
        DateTime approvedAtUtc, DateTime invoiceIssuedAtUtc,
        decimal verifiedHeldAmount, decimal outstandingDebt,
        decimal explicitlyApprovedForfeitAmount,
        string? damageReason, string? damageEvidenceReference,
        string explicitDepositPolicyReference)
    {
        var reason = string.IsNullOrWhiteSpace(damageReason)
            ? null : damageReason.Trim();
        var evidence = string.IsNullOrWhiteSpace(damageEvidenceReference)
            ? null : damageEvidenceReference.Trim();
        var policy = explicitDepositPolicyReference?.Trim() ?? string.Empty;
        if (id == Guid.Empty || contractId == Guid.Empty ||
            moveOutRequestId == Guid.Empty || finalIssuedInvoiceId == Guid.Empty ||
            buildingId == Guid.Empty || actorId == Guid.Empty ||
            approvedAtUtc.Kind != DateTimeKind.Utc ||
            invoiceIssuedAtUtc.Kind != DateTimeKind.Utc ||
            approvedAtUtc < invoiceIssuedAtUtc ||
            verifiedHeldAmount <= 0m || !Whole(verifiedHeldAmount) ||
            outstandingDebt != 0m ||
            explicitlyApprovedForfeitAmount < 0m ||
            !Whole(explicitlyApprovedForfeitAmount) ||
            explicitlyApprovedForfeitAmount > verifiedHeldAmount ||
            policy.Length is < 8 or > 200 ||
            (explicitlyApprovedForfeitAmount > 0m &&
             (reason?.Length is null or < 8 or > 500 ||
              evidence?.Length is null or < 8 or > 500)) ||
            (explicitlyApprovedForfeitAmount == 0m &&
             (!string.IsNullOrWhiteSpace(reason) ||
              !string.IsNullOrWhiteSpace(evidence))))
            throw new InvalidOperationException(
                "Deposit decision requires verified held funds, zero outstanding debt, " +
                "explicit owner damage evidence and a documented no-offset policy.");

        var at = approvedAtUtc.AddTicks(-(approvedAtUtc.Ticks % 10L));
        var refund = verifiedHeldAmount - explicitlyApprovedForfeitAmount;
        var json = JsonSerializer.Serialize(new
        {
            ContractId = contractId,
            MoveOutRequestId = moveOutRequestId,
            FinalIssuedInvoiceId = finalIssuedInvoiceId,
            BuildingId = buildingId,
            ApprovedByUserId = actorId,
            ApprovedAtUtc = at,
            VerifiedHeldAmount = verifiedHeldAmount,
            ApprovedForfeitAmount = explicitlyApprovedForfeitAmount,
            ApprovedRefundAmount = refund,
            ApprovedDebtOffsetAmount = 0m,
            DamageReason = reason,
            DamageEvidenceReference = evidence,
            ExplicitDepositPolicyReference = policy
        });
        if (json.Length > 1000000)
            throw new InvalidOperationException("Decision proof is too large.");

        var decision = new ContractDepositSettlementDecisionRecord(id)
        {
            ContractId = contractId,
            MoveOutRequestId = moveOutRequestId,
            FinalIssuedInvoiceId = finalIssuedInvoiceId,
            BuildingId = buildingId,
            ApprovedByUserId = actorId,
            ApprovedAtUtc = at,
            VerifiedHeldAmount = verifiedHeldAmount,
            ApprovedForfeitAmount = explicitlyApprovedForfeitAmount,
            ApprovedRefundAmount = refund,
            ApprovedDebtOffsetAmount = 0m,
            DamageReason = reason,
            DamageEvidenceReference = evidence,
            ExplicitDepositPolicyReference = policy,
            ApprovalJson = json,
            ApprovalSha256 = Hash(json)
        };
        decision.VerifyStoredDecision();
        return decision;
    }

    public void VerifyStoredDecision()
    {
        if (Id == Guid.Empty || ContractId == Guid.Empty ||
            MoveOutRequestId == Guid.Empty || FinalIssuedInvoiceId == Guid.Empty ||
            BuildingId == Guid.Empty || ApprovedByUserId == Guid.Empty ||
            ApprovedAtUtc.Kind != DateTimeKind.Utc ||
            VerifiedHeldAmount <= 0m || !Whole(VerifiedHeldAmount) ||
            ApprovedDebtOffsetAmount != 0m ||
            ApprovedForfeitAmount < 0m || !Whole(ApprovedForfeitAmount) ||
            ApprovedRefundAmount < 0m || !Whole(ApprovedRefundAmount) ||
            VerifiedHeldAmount != ApprovedForfeitAmount + ApprovedRefundAmount ||
            ExplicitDepositPolicyReference?.Trim().Length is < 8 or > 200 ||
            (ApprovedForfeitAmount > 0m &&
             (DamageReason?.Trim().Length is null or < 8 or > 500 ||
              DamageEvidenceReference?.Trim().Length is null or < 8 or > 500)) ||
            (ApprovedForfeitAmount == 0m &&
             (!string.IsNullOrEmpty(DamageReason) ||
              !string.IsNullOrEmpty(DamageEvidenceReference))) ||
            string.IsNullOrEmpty(ApprovalJson) ||
            ApprovalJson.Length > 1000000 ||
            ApprovalSha256 != Hash(ApprovalJson))
            throw new InvalidDataException("Deposit decision archive is inconsistent.");

        try
        {
            using var json = JsonDocument.Parse(ApprovalJson);
            var root = json.RootElement;
            if (root.GetProperty("ContractId").GetGuid() != ContractId ||
                root.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                root.GetProperty("FinalIssuedInvoiceId").GetGuid() != FinalIssuedInvoiceId ||
                root.GetProperty("BuildingId").GetGuid() != BuildingId ||
                root.GetProperty("ApprovedByUserId").GetGuid() != ApprovedByUserId ||
                root.GetProperty("ApprovedAtUtc").GetDateTime() != ApprovedAtUtc ||
                root.GetProperty("VerifiedHeldAmount").GetDecimal() != VerifiedHeldAmount ||
                root.GetProperty("ApprovedForfeitAmount").GetDecimal() != ApprovedForfeitAmount ||
                root.GetProperty("ApprovedRefundAmount").GetDecimal() != ApprovedRefundAmount ||
                root.GetProperty("ApprovedDebtOffsetAmount").GetDecimal() != 0m ||
                Text(root, "DamageReason") != DamageReason ||
                Text(root, "DamageEvidenceReference") != DamageEvidenceReference ||
                root.GetProperty("ExplicitDepositPolicyReference").GetString() !=
                    ExplicitDepositPolicyReference)
                throw new InvalidDataException("Deposit decision proof differs from indexed fields.");
        }
        catch (Exception ex) when (ex is JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Deposit decision JSON is invalid.", ex);
        }
    }

    public void RequireForCloseout(Guid contractId, Guid requestId,
        Guid finalInvoiceId, Guid buildingId, decimal held,
        decimal forfeit, string? reason, string? evidence, string policy)
    {
        VerifyStoredDecision();
        if (ContractId != contractId || MoveOutRequestId != requestId ||
            FinalIssuedInvoiceId != finalInvoiceId || BuildingId != buildingId ||
            VerifiedHeldAmount != held || ApprovedForfeitAmount != forfeit ||
            !StringComparer.Ordinal.Equals(DamageReason,
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()) ||
            !StringComparer.Ordinal.Equals(DamageEvidenceReference,
                string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim()) ||
            !StringComparer.Ordinal.Equals(ExplicitDepositPolicyReference, policy?.Trim()))
            throw new InvalidOperationException(
                "Final closeout must exactly match the independently committed deposit decision.");
    }

    private static string? Text(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static bool Whole(decimal amount) => decimal.Truncate(amount) == amount;
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
