using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

/// <summary>
/// Persisted closeout metadata must match every archived financial/proof
/// field, not merely the invoice/contract/amount fields. Read-only checks.
/// </summary>
public sealed class ContractDepositCloseoutApprovalIntegrity_Tests
{
    private static readonly DateTime DecisionTime =
        new(2026, 10, 29, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BankTime =
        new(2026, 10, 29, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Complete_Archived_Approval_And_Index_Metadata_Match()
    {
        var approval = PrepareHydrated();
        approval.VerifyStoredApproval();
        Assert.False(approval.IsAutomaticDebtOffset);
        Assert.False(approval.IsVerifiedBankTransfer);
    }

    [Theory]
    [InlineData("ApprovedAtUtc")]
    [InlineData("RefundSettlementId")]
    [InlineData("ForfeitSettlementId")]
    [InlineData("RefundTransactionReference")]
    [InlineData("RefundPaidAtUtc")]
    [InlineData("DamageReason")]
    [InlineData("DamageEvidenceReference")]
    [InlineData("InspectionEvidenceReference")]
    [InlineData("HandoverEvidenceReference")]
    [InlineData("ExplicitDepositPolicyReference")]
    [InlineData("ApprovedByUserId")]
    public void Rewritten_Indexed_Proof_With_Same_Valid_Archive_Hash_Is_Rejected(
        string key)
    {
        var approval = PrepareHydrated();
        var changed = key switch
        {
            "ApprovedAtUtc" => (object)DecisionTime.AddMinutes(-1),
            "RefundSettlementId" => Guid.NewGuid(),
            "ForfeitSettlementId" => Guid.NewGuid(),
            "RefundPaidAtUtc" => BankTime.AddMinutes(1),
            "ApprovedByUserId" => Guid.NewGuid(),
            _ => "Changed evidence/reference while retaining archived digest"
        };
        Set(approval, key, changed);
        Assert.Throws<InvalidDataException>(
            approval.VerifyStoredApproval);
    }

    [Fact]
    public void Tampered_ApprovalJson_Hash_Is_Rejected()
    {
        var approval = PrepareHydrated();
        Set(approval, "ApprovalJson", approval.ApprovalJson + " ");
        Assert.Throws<InvalidDataException>(
            approval.VerifyStoredApproval);
    }

    private static ContractDepositCloseoutApprovalRecord PrepareHydrated()
    {
        var id = Guid.NewGuid();
        var contract = Guid.NewGuid();
        var request = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var building = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var forfeitId = Guid.NewGuid();
        const string transaction = "MANUAL-REFUND-2026-0001";
        const string damage = "Documented cracked floor tile";
        const string damageEvidence = "INSPECTION-DAMAGE-PHOTO-0001";
        const string inspection = "INSPECTION-SIGNED-0001";
        const string handover = "HANDOVER-SIGNED-0001";
        const string policy = "AGREED-DEPOSIT-POLICY-0001";

        var json = JsonSerializer.Serialize(new
        {
            ContractId = contract, MoveOutRequestId = request,
            IssuedInvoiceId = invoice, BuildingId = building,
            ApprovedByUserId = actor, ApprovedAtUtc = DecisionTime,
            VerifiedHeldAmount = 1000000m,
            ApprovedForfeitAmount = 200000m,
            ApprovedRefundAmount = 800000m,
            RefundSettlementId = (Guid?)refundId,
            ForfeitSettlementId = (Guid?)forfeitId,
            RefundTransactionReference = transaction,
            RefundPaidAtUtc = (DateTime?)BankTime,
            DamageReason = damage,
            DamageEvidenceReference = damageEvidence,
            InspectionEvidenceReference = inspection,
            HandoverEvidenceReference = handover,
            ExplicitDepositPolicyReference = policy
        });

        var ctor = typeof(ContractDepositCloseoutApprovalRecord)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Guid) },
                modifiers: null);
        Assert.NotNull(ctor);
        var approval =
            (ContractDepositCloseoutApprovalRecord)ctor.Invoke(new object[] { id });
        Set(approval, "ContractId", contract);
        Set(approval, "MoveOutRequestId", request);
        Set(approval, "IssuedInvoiceId", invoice);
        Set(approval, "BuildingId", building);
        Set(approval, "ApprovedByUserId", actor);
        Set(approval, "ApprovedAtUtc", DecisionTime);
        Set(approval, "VerifiedHeldAmount", 1000000m);
        Set(approval, "ApprovedForfeitAmount", 200000m);
        Set(approval, "ApprovedRefundAmount", 800000m);
        Set(approval, "RefundSettlementId", refundId);
        Set(approval, "ForfeitSettlementId", forfeitId);
        Set(approval, "RefundTransactionReference", transaction);
        Set(approval, "RefundPaidAtUtc", BankTime);
        Set(approval, "DamageReason", damage);
        Set(approval, "DamageEvidenceReference", damageEvidence);
        Set(approval, "InspectionEvidenceReference", inspection);
        Set(approval, "HandoverEvidenceReference", handover);
        Set(approval, "ExplicitDepositPolicyReference", policy);
        Set(approval, "ApprovalJson", json);
        Set(approval, "ApprovalSha256",
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
                .ToLowerInvariant());
        return approval;
    }

    private static void Set(
        ContractDepositCloseoutApprovalRecord record,
        string propertyName, object value)
    {
        var prop = typeof(ContractDepositCloseoutApprovalRecord)
            .GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(prop);
        prop.SetValue(record, value);
    }
}
