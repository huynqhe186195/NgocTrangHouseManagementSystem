using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// D01/D02: a separate documented Owner decision on the exact immutable
/// day-owner, VND rounding and old/new tariffs. The original OI-07 policy
/// review occurred BEFORE those values were selected; it cannot be
/// mistaken for approval of the later numeric choice.
/// This is not a universal tariff, invoice posting or transfer permission.
/// </summary>
public sealed class RoomChangeDayPricingOwnerSignoff
{
    public Guid Id { get; private init; }
    public Guid RequestId { get; private init; }
    public Guid PricingRuleId { get; private init; }
    public string PricingRuleSha256 { get; private init; } = string.Empty;
    public Guid OwnerUserId { get; private init; }
    public DateTime SignedAtUtc { get; private init; }
    public string SignedPricingApprovalReference { get; private init; } = string.Empty;
    public string EvidenceSha256 { get; private init; } = string.Empty;

    private RoomChangeDayPricingOwnerSignoff() { }

    public static RoomChangeDayPricingOwnerSignoff Sign(
        Guid id, RoomChangeDayPricingRuleCandidate rule,
        RoomChangeRequest request, Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord policyReview,
        Guid ownerUserId, DateTime signedAtUtc, string? signedReference)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Verify(request, source, roster, policy, policyReview);
        var reference = signedReference?.Trim() ?? string.Empty;
        if (id == Guid.Empty || ownerUserId == Guid.Empty ||
            !policyReview.Accepted ||
            ownerUserId != policyReview.ReviewedByUserId ||
            ownerUserId == rule.PreparedByUserId ||
            signedAtUtc.Kind != DateTimeKind.Utc ||
            signedAtUtc < rule.PreparedAtUtc ||
            reference.Length is < 12 or > 500 ||
            rule.RequestId != request.Id ||
            rule.PolicyReviewId != policyReview.Id)
            throw new InvalidOperationException(
                "D01/D02 needs Owner-signed approval of the exact later pricing candidate.");

        var signoff = new RoomChangeDayPricingOwnerSignoff
        {
            Id = id,
            RequestId = request.Id,
            PricingRuleId = rule.Id,
            PricingRuleSha256 = rule.EvidenceSha256,
            OwnerUserId = ownerUserId,
            SignedAtUtc = Normalize(signedAtUtc),
            SignedPricingApprovalReference = reference
        };
        return signoff withHash();
    }

    // A new immutable value is returned. No public "set approved" switch.
    private RoomChangeDayPricingOwnerSignoff withHash()
        => new()
        {
            Id = Id, RequestId = RequestId,
            PricingRuleId = PricingRuleId,
            PricingRuleSha256 = PricingRuleSha256,
            OwnerUserId = OwnerUserId, SignedAtUtc = SignedAtUtc,
            SignedPricingApprovalReference = SignedPricingApprovalReference,
            EvidenceSha256 = Hash()
        };

    public void Verify(
        RoomChangeDayPricingRuleCandidate rule,
        RoomChangeRequest request, Contract source,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord policyReview)
    {
        if (Id == Guid.Empty || RequestId != request.Id ||
            PricingRuleId != rule.Id ||
            PricingRuleSha256 != rule.EvidenceSha256 ||
            OwnerUserId == Guid.Empty ||
            OwnerUserId != policyReview.ReviewedByUserId ||
            OwnerUserId == rule.PreparedByUserId ||
            SignedAtUtc.Kind != DateTimeKind.Utc ||
            SignedAtUtc < rule.PreparedAtUtc ||
            SignedPricingApprovalReference.Length is < 12 or > 500 ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("D01/D02 signed pricing choice has drifted.");
        try { rule.Verify(request, source, roster, policy, policyReview); }
        catch (Exception e) when (e is InvalidOperationException or
            InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException("Underlying pricing choice no longer matches OI-07.", e);
        }
    }

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            PricingRuleId.ToString("D"), PricingRuleSha256,
            OwnerUserId.ToString("D"),
            SignedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            SignedPricingApprovalReference
        });
        return Sha(canonical);
    }

    public bool CanPostInvoice => false;
    public bool CanExecuteTransfer => false;

    private static DateTime Normalize(DateTime instant)
        => instant.AddTicks(-(instant.Ticks % 10));

    internal static string Sha(string value)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// D06: signed asset/defect checklist and three independent roles anchored
/// to the actual H6 source meter evidence and exact source roster. The
/// tenant representative must be the recorded primary tenant. A document
/// reference is an operator attestation, NOT cryptographic identity proof.
/// </summary>
public sealed class RoomChangeHandoverThreePartySignoff
{
    public Guid Id { get; private init; }
    public Guid RequestId { get; private init; }
    public Guid RosterSnapshotId { get; private init; }
    public string RosterSha256 { get; private init; } = string.Empty;
    public Guid HandoverId { get; private init; }
    public string PhysicalHandoverSha256 { get; private init; } = string.Empty;
    public Guid OwnerSignerId { get; private init; }
    public Guid StaffSignerId { get; private init; }
    public Guid TenantRepresentativeId { get; private init; }
    public string OwnerSignedReference { get; private init; } = string.Empty;
    public string StaffSignedReference { get; private init; } = string.Empty;
    public string TenantSignedReference { get; private init; } = string.Empty;
    public string AssetDefectReviewReference { get; private init; } = string.Empty;
    public DateTime SignedAtUtc { get; private init; }
    public string EvidenceSha256 { get; private init; } = string.Empty;

    private RoomChangeHandoverThreePartySignoff() { }

    public static RoomChangeHandoverThreePartySignoff Sign(
        Guid id, RoomChangeRequest request,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeHandoverEvidenceRecord physical,
        Guid ownerSignerId, Guid staffSignerId,
        Guid tenantRepresentativeId,
        string? ownerSignedReference, string? staffSignedReference,
        string? tenantSignedReference, string? assetDefectReviewReference,
        DateTime signedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(physical);
        var parties = roster.ReadVerifiedRoster();
        var primary = parties.Single(x => x.Role == ContractTenantRole.PrimaryTenant);
        var owner = ownerSignedReference?.Trim() ?? string.Empty;
        var staff = staffSignedReference?.Trim() ?? string.Empty;
        var tenant = tenantSignedReference?.Trim() ?? string.Empty;
        var defects = assetDefectReviewReference?.Trim() ?? string.Empty;

        if (id == Guid.Empty || request.Id == Guid.Empty ||
            roster.RoomChangeRequestId != request.Id ||
            roster.SourceContractId != request.SourceContractId ||
            roster.SourceRoomId != request.SourceRoomId ||
            roster.BuildingId != request.BuildingId ||
            physical.RequestId != request.Id ||
            physical.SourceContractId != request.SourceContractId ||
            physical.SourceRoomId != request.SourceRoomId ||
            physical.BuildingId != request.BuildingId ||
            physical.HandoverDate != request.RequestedDate ||
            physical.EvidenceSha256.Length != 64 ||
            physical.ElectricityReadingId == Guid.Empty ||
            physical.WaterReadingId == Guid.Empty ||
            ownerSignerId == Guid.Empty || staffSignerId == Guid.Empty ||
            tenantRepresentativeId == Guid.Empty ||
            staffSignerId != physical.AttestedByUserId ||
            tenantRepresentativeId != primary.TenantId ||
            ownerSignerId == staffSignerId ||
            ownerSignerId == tenantRepresentativeId ||
            staffSignerId == tenantRepresentativeId ||
            signedAtUtc.Kind != DateTimeKind.Utc ||
            signedAtUtc < physical.AttestedAtUtc ||
            signedAtUtc < roster.CapturedAtUtc ||
            !Ref(owner) || !Ref(staff) || !Ref(tenant) || !Ref(defects) ||
            owner == staff || owner == tenant || staff == tenant)
            throw new InvalidOperationException(
                "H6/D06 requires distinct Owner, handover staff and recorded primary tenant signoffs.");

        var candidate = new RoomChangeHandoverThreePartySignoff
        {
            Id = id, RequestId = request.Id,
            RosterSnapshotId = roster.Id, RosterSha256 = roster.RosterSha256,
            HandoverId = physical.Id, PhysicalHandoverSha256 = physical.EvidenceSha256,
            OwnerSignerId = ownerSignerId, StaffSignerId = staffSignerId,
            TenantRepresentativeId = tenantRepresentativeId,
            OwnerSignedReference = owner, StaffSignedReference = staff,
            TenantSignedReference = tenant,
            AssetDefectReviewReference = defects,
            SignedAtUtc = signedAtUtc.AddTicks(-(signedAtUtc.Ticks % 10))
        };
        return new RoomChangeHandoverThreePartySignoff
        {
            Id = candidate.Id, RequestId = candidate.RequestId,
            RosterSnapshotId = candidate.RosterSnapshotId,
            RosterSha256 = candidate.RosterSha256,
            HandoverId = candidate.HandoverId,
            PhysicalHandoverSha256 = candidate.PhysicalHandoverSha256,
            OwnerSignerId = candidate.OwnerSignerId,
            StaffSignerId = candidate.StaffSignerId,
            TenantRepresentativeId = candidate.TenantRepresentativeId,
            OwnerSignedReference = candidate.OwnerSignedReference,
            StaffSignedReference = candidate.StaffSignedReference,
            TenantSignedReference = candidate.TenantSignedReference,
            AssetDefectReviewReference = candidate.AssetDefectReviewReference,
            SignedAtUtc = candidate.SignedAtUtc,
            EvidenceSha256 = candidate.Hash()
        };
    }

    public void Verify(RoomChangeRequest request,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeHandoverEvidenceRecord physical)
    {
        if (Id == Guid.Empty || EvidenceSha256 != Hash() ||
            RequestId != request.Id ||
            HandoverId != physical.Id ||
            PhysicalHandoverSha256 != physical.EvidenceSha256 ||
            RosterSnapshotId != roster.Id ||
            RosterSha256 != roster.RosterSha256)
            throw new InvalidDataException("D06 signed evidence fingerprint or source is incorrect.");
        try
        {
            var rebuilt = Sign(Id, request, roster, physical,
                OwnerSignerId, StaffSignerId, TenantRepresentativeId,
                OwnerSignedReference, StaffSignedReference,
                TenantSignedReference, AssetDefectReviewReference, SignedAtUtc);
            if (rebuilt.EvidenceSha256 != EvidenceSha256)
                throw new InvalidDataException("D06 signed evidence drift.");
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("D06 signatories differ from source handover.", e);
        }
    }

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            RosterSnapshotId.ToString("D"), RosterSha256,
            HandoverId.ToString("D"), PhysicalHandoverSha256,
            OwnerSignerId.ToString("D"), StaffSignerId.ToString("D"),
            TenantRepresentativeId.ToString("D"), OwnerSignedReference,
            StaffSignedReference, TenantSignedReference,
            AssetDefectReviewReference,
            SignedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
        return RoomChangeDayPricingOwnerSignoff.Sha(canonical);
    }

    private static bool Ref(string value) =>
        value.Length is >= 12 and <= 500;

    public bool CanExecuteTransfer => false;
    public bool IsCryptographicallyAuthenticatedSignature => false;
}
