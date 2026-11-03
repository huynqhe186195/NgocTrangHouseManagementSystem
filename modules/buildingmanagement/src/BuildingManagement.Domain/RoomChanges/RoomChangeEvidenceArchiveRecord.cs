using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Persisted append-only documentary archives. NONE of these rows posts a
/// receivable, assigns the source deposit, proves a real bank payout, or
/// ends/creates a contract. A caller MUST reload all original evidence
/// under scoped locks before capture. Human signatures are references.
/// </summary>
public enum RoomChangeEvidenceArchiveKind
{
    SignedExactPricing = 1,
    ClassifiedInvoiceCorrection = 2,
    ReviewedTransferCash = 3,
    ThreePartyPhysicalHandover = 4
}

public sealed class RoomChangeEvidenceArchiveRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public RoomChangeEvidenceArchiveKind Kind { get; private set; }
    public Guid EvidenceId { get; private set; }
    public string SourceEvidenceSha256 { get; private set; } = string.Empty;
    public Guid ParentEvidenceId { get; private set; }
    public string ParentEvidenceSha256 { get; private set; } = string.Empty;
    public string EvidenceJson { get; private set; } = string.Empty;
    public string EvidenceJsonSha256 { get; private set; } = string.Empty;
    public Guid ArchivedByUserId { get; private set; }
    public DateTime ArchivedAtUtc { get; private set; }
    public string RecordSha256 { get; private set; } = string.Empty;

    // Explicitly non-executing by design; archive != financial posting.
    public bool CanPostReceivable => false;
    public bool CanRefundDeposit => false;
    public bool CanExecuteRoomChange => false;

    private RoomChangeEvidenceArchiveRecord() { }
    private RoomChangeEvidenceArchiveRecord(Guid id) : base(id) { }

    public static RoomChangeEvidenceArchiveRecord CapturePricing(
        Guid id, RoomChangeRequest request, Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review,
        RoomChangeDayPricingRuleCandidate dayRule,
        RoomChangeDayPricingOwnerSignoff signoff,
        DateTime archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(signoff);
        signoff.Verify(dayRule, request, source, roster, policy, review);
        return Create(id, request, RoomChangeEvidenceArchiveKind.SignedExactPricing,
            signoff.Id, signoff.EvidenceSha256, dayRule.Id,
            dayRule.EvidenceSha256, signoff.OwnerUserId,
            signoff.SignedAtUtc, signoff, archivedAtUtc);
    }

    public static RoomChangeEvidenceArchiveRecord CaptureClassifiedCorrection(
        Guid id, RoomChangeRequest request,
        MonthlyIssuedInvoiceRecord issued,
        MonthlyInvoiceCandidateLedgerRecord frozen,
        MonthlyInvoiceIndependentReviewRecord issuanceReview,
        MonthlyInvoiceAdjustmentRecord adjustment,
        MonthlyInvoiceAdjustmentDecisionRecord decision,
        RoomChangeIssuedAdjustmentCategoryProof classification,
        DateTime archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(issuanceReview);
        ArgumentNullException.ThrowIfNull(classification);
        issued.VerifyAgainst(frozen, issuanceReview);
        classification.Verify(issued, adjustment, decision);
        if (request.SourceContractId != issued.ContractId ||
            request.BuildingId != issued.BuildingId ||
            request.SourceRoomId != issued.RoomId ||
            request.RequestedDate.Year != issued.BillingYear ||
            request.RequestedDate.Month != issued.BillingMonth)
            throw new InvalidOperationException(
                "D03 adjustment is for a different source contract, room or transfer month.");
        return Create(id, request,
            RoomChangeEvidenceArchiveKind.ClassifiedInvoiceCorrection,
            classification.Id, classification.EvidenceSha256,
            issued.Id, issued.CandidateSha256, classification.ClassifiedByUserId,
            classification.ClassifiedAtUtc, classification, archivedAtUtc);
    }

    public static RoomChangeEvidenceArchiveRecord CaptureCashReview(
        Guid id, RoomChangeRequest request, Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review,
        RoomChangeMidMonthFinancialReconciliationPacket packet,
        RoomChangeTransferDepositCashReview cash,
        Payment? completedTargetTopUp, DateTime archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(cash);
        ArgumentNullException.ThrowIfNull(packet);
        packet.VerifyAgainst(request, source, roster, policy, review);
        cash.Verify(packet, completedTargetTopUp);
        return Create(id, request, RoomChangeEvidenceArchiveKind.ReviewedTransferCash,
            cash.Id, cash.EvidenceSha256, packet.PacketId,
            packet.EvidenceSha256, cash.IndependentBankReviewerId,
            cash.ReviewedAtUtc, cash, archivedAtUtc);
    }

    public static RoomChangeEvidenceArchiveRecord CaptureThreePartyHandover(
        Guid id, RoomChangeRequest request,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeHandoverEvidenceRecord handover,
        UtilityMeter electricityMeter, MeterReading electricityReading,
        UtilityMeter waterMeter, MeterReading waterReading,
        RoomChangeHandoverThreePartySignoff signatures,
        DateTime archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        handover.Verify(request, electricityMeter, electricityReading,
            waterMeter, waterReading);
        signatures.Verify(request, roster, handover);
        return Create(id, request,
            RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover,
            signatures.Id, signatures.EvidenceSha256,
            handover.Id, handover.EvidenceSha256,
            signatures.OwnerSignerId, signatures.SignedAtUtc,
            signatures, archivedAtUtc);
    }

    private static RoomChangeEvidenceArchiveRecord Create(
        Guid id, RoomChangeRequest request, RoomChangeEvidenceArchiveKind kind,
        Guid evidenceId, string evidenceSha, Guid parentId, string parentSha,
        Guid archivist, DateTime signedAt, object evidence,
        DateTime archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var at = archivedAtUtc.Kind == DateTimeKind.Utc
            ? archivedAtUtc.AddTicks(-(archivedAtUtc.Ticks % 10)) : archivedAtUtc;
        if (id == Guid.Empty || request.Id == Guid.Empty ||
            request.SourceContractId == Guid.Empty || request.BuildingId == Guid.Empty ||
            request.SourceRoomId == Guid.Empty ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            !Enum.IsDefined(kind) || evidenceId == Guid.Empty ||
            parentId == Guid.Empty || archivist == Guid.Empty ||
            !ValidSha(evidenceSha) || !ValidSha(parentSha) ||
            signedAt.Kind != DateTimeKind.Utc ||
            at.Kind != DateTimeKind.Utc ||
            at < signedAt || at < request.CreatedAtUtc)
            throw new InvalidOperationException(
                "Staging documentary archive requires the original, signed, scoped source.");

        var json = JsonSerializer.Serialize(evidence);
        // A stored JSON payload must be the exact (typed) original snapshot.
        // It is NOT an authenticated human signature or a bank API statement.
        var record = new RoomChangeEvidenceArchiveRecord(id)
        {
            RequestId = request.Id,
            SourceContractId = request.SourceContractId,
            BuildingId = request.BuildingId,
            Kind = kind,
            EvidenceId = evidenceId,
            SourceEvidenceSha256 = evidenceSha,
            ParentEvidenceId = parentId,
            ParentEvidenceSha256 = parentSha,
            EvidenceJson = json,
            EvidenceJsonSha256 = Sha(json),
            ArchivedByUserId = archivist,
            ArchivedAtUtc = at
        };
        record.RecordSha256 = record.Hash();
        record.VerifyStoredSnapshot();
        return record;
    }

    public void VerifyStoredSnapshot()
    {
        if (Id == Guid.Empty || RequestId == Guid.Empty ||
            SourceContractId == Guid.Empty || BuildingId == Guid.Empty ||
            EvidenceId == Guid.Empty || ParentEvidenceId == Guid.Empty ||
            ArchivedByUserId == Guid.Empty ||
            !Enum.IsDefined(Kind) ||
            ArchivedAtUtc.Kind != DateTimeKind.Utc ||
            !ValidSha(SourceEvidenceSha256) || !ValidSha(ParentEvidenceSha256) ||
            !ValidSha(EvidenceJsonSha256) || !ValidSha(RecordSha256) ||
            EvidenceJson.Length is < 10 or > 200000 ||
            EvidenceJsonSha256 != Sha(EvidenceJson) ||
            RecordSha256 != Hash())
            throw new InvalidDataException("2H immutable documentary archive fingerprint is invalid.");
        try
        {
            using var doc = JsonDocument.Parse(EvidenceJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("Id").GetGuid() != EvidenceId ||
                root.GetProperty("EvidenceSha256").GetString() != SourceEvidenceSha256)
                throw new InvalidDataException("Documentary evidence payload does not match archived identity.");
            string linkedId;
            string linkedSha;
            switch (Kind)
            {
                case RoomChangeEvidenceArchiveKind.SignedExactPricing:
                    linkedId = "PricingRuleId";
                    linkedSha = "PricingRuleSha256";
                    if (root.GetProperty("RequestId").GetGuid() != RequestId ||
                        root.GetProperty("OwnerUserId").GetGuid() != ArchivedByUserId)
                        throw new InvalidDataException("D01/D02 Owner approval is detached.");
                    break;
                case RoomChangeEvidenceArchiveKind.ClassifiedInvoiceCorrection:
                    linkedId = "IssuedInvoiceId";
                    linkedSha = "OriginalInvoiceSha256";
                    if (root.GetProperty("ContractId").GetGuid() != SourceContractId ||
                        root.GetProperty("BuildingId").GetGuid() != BuildingId ||
                        root.GetProperty("ClassifiedByUserId").GetGuid() != ArchivedByUserId)
                        throw new InvalidDataException("D03 scope is detached.");
                    break;
                case RoomChangeEvidenceArchiveKind.ReviewedTransferCash:
                    linkedId = "FinancialPacketId";
                    linkedSha = "FinancialPacketSha256";
                    if (root.GetProperty("RequestId").GetGuid() != RequestId ||
                        root.GetProperty("SourceContractId").GetGuid() != SourceContractId ||
                        root.GetProperty("IndependentBankReviewerId").GetGuid() != ArchivedByUserId)
                        throw new InvalidDataException("D04 source packet is detached.");
                    break;
                case RoomChangeEvidenceArchiveKind.ThreePartyPhysicalHandover:
                    linkedId = "HandoverId";
                    linkedSha = "PhysicalHandoverSha256";
                    if (root.GetProperty("RequestId").GetGuid() != RequestId ||
                        root.GetProperty("OwnerSignerId").GetGuid() != ArchivedByUserId)
                        throw new InvalidDataException("D06 handover is detached.");
                    break;
                default:
                    throw new InvalidDataException("Unknown evidence kind.");
            }

            if (root.GetProperty(linkedId).GetGuid() != ParentEvidenceId ||
                root.GetProperty(linkedSha).GetString() != ParentEvidenceSha256)
                throw new InvalidDataException("Archived documentary parent evidence differs.");
        }
        catch (Exception ex) when (ex is JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("2H archive serialized documentary evidence invalid.", ex);
        }
    }

    private string Hash()
    {
        var parts = new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), BuildingId.ToString("D"),
            ((int)Kind).ToString(CultureInfo.InvariantCulture),
            EvidenceId.ToString("D"), SourceEvidenceSha256,
            ParentEvidenceId.ToString("D"), ParentEvidenceSha256,
            EvidenceJsonSha256, ArchivedByUserId.ToString("D"),
            ArchivedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        };
        return Sha(string.Join("\n", parts));
    }

    private static string Sha(string s)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static bool ValidSha(string? value)
        => value is { Length: 64 } &&
            Array.TrueForAll(value.ToCharArray(), c =>
                c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
