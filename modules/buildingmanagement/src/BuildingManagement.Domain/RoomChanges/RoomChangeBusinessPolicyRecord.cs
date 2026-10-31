using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Four OI-07 choices are an EXPLICIT proposal, not approved global
/// business policy. It neither proves signed handover nor moves funds.
/// Each reference is a trace to independently retained signed evidence.
/// </summary>
public enum RoomChangeContractTreatment { NewContract = 1, VersionedAddendum = 2 }
public enum RoomChangePricingTreatment { FullOldMonthNewNextMonth = 1, AgreedDayProration = 2 }
public enum RoomChangeDepositTreatment { CarryAllWithNoDifference = 1, CarryWithVerifiedTopUp = 2, PartialCarryWithRefund = 3 }
public enum RoomChangeTenantTreatment { AllCurrentTenants = 1, ConsentingSubset = 2 }

public sealed class RoomChangeBusinessPolicyRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid RosterSnapshotId { get; private set; }
    public string RosterSha256 { get; private set; } = string.Empty;
    public RoomChangeContractTreatment ContractTreatment { get; private set; }
    public RoomChangePricingTreatment PricingTreatment { get; private set; }
    public RoomChangeDepositTreatment DepositTreatment { get; private set; }
    public RoomChangeTenantTreatment TenantTreatment { get; private set; }
    public string ContractPolicyReference { get; private set; } = string.Empty;
    public string PricingPolicyReference { get; private set; } = string.Empty;
    public string DepositPolicyReference { get; private set; } = string.Empty;
    public string TenantConsentReference { get; private set; } = string.Empty;
    public Guid ProposedByUserId { get; private set; }
    public DateTime ProposedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeBusinessPolicyRecord() { }
    private RoomChangeBusinessPolicyRecord(Guid id) : base(id) { }

    public static RoomChangeBusinessPolicyRecord Propose(
        Guid id, RoomChangeRequest request,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeContractTreatment contractTreatment,
        RoomChangePricingTreatment pricingTreatment,
        RoomChangeDepositTreatment depositTreatment,
        RoomChangeTenantTreatment tenantTreatment,
        string? contractPolicyReference, string? pricingPolicyReference,
        string? depositPolicyReference, string? tenantConsentReference,
        Guid actor, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roster);
        roster.ReadVerifiedRoster();
        if (id == Guid.Empty || actor == Guid.Empty ||
            atUtc.Kind != DateTimeKind.Utc || atUtc < roster.CapturedAtUtc ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            request.Id != roster.RoomChangeRequestId ||
            request.SourceContractId != roster.SourceContractId ||
            request.BuildingId != roster.BuildingId ||
            request.SourceRoomId != roster.SourceRoomId ||
            !Enum.IsDefined(contractTreatment) ||
            !Enum.IsDefined(pricingTreatment) ||
            !Enum.IsDefined(depositTreatment) ||
            !Enum.IsDefined(tenantTreatment))
            throw new InvalidOperationException("OI-07 proposal requires valid immutable request and roster.");

        var c = Reference(contractPolicyReference);
        var p = Reference(pricingPolicyReference);
        var d = Reference(depositPolicyReference);
        var t = Reference(tenantConsentReference);
        var result = new RoomChangeBusinessPolicyRecord(id)
        {
            RequestId = request.Id,
            SourceContractId = request.SourceContractId,
            BuildingId = request.BuildingId,
            RosterSnapshotId = roster.Id,
            RosterSha256 = roster.RosterSha256,
            ContractTreatment = contractTreatment, PricingTreatment = pricingTreatment,
            DepositTreatment = depositTreatment, TenantTreatment = tenantTreatment,
            ContractPolicyReference = c, PricingPolicyReference = p,
            DepositPolicyReference = d, TenantConsentReference = t,
            ProposedByUserId = actor,
            ProposedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10))
        };
        result.EvidenceSha256 = result.CalculateHash();
        return result;
    }

    public void Verify(RoomChangeRequest request, RoomChangeTenantRosterSnapshotRecord roster)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(roster);
        roster.ReadVerifiedRoster();
        if (Id == Guid.Empty || RequestId != request.Id ||
            SourceContractId != request.SourceContractId ||
            BuildingId != request.BuildingId ||
            RosterSnapshotId != roster.Id ||
            RosterSha256 != roster.RosterSha256 ||
            ProposedByUserId == Guid.Empty ||
            ProposedAtUtc.Kind != DateTimeKind.Utc ||
            ProposedAtUtc < roster.CapturedAtUtc ||
            !Enum.IsDefined(ContractTreatment) ||
            !Enum.IsDefined(PricingTreatment) ||
            !Enum.IsDefined(DepositTreatment) ||
            !Enum.IsDefined(TenantTreatment) ||
            EvidenceSha256 != CalculateHash())
            throw new InvalidDataException("OI-07 proposal does not match immutable evidence.");
    }

    private static string Reference(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 12 or > 500)
            throw new InvalidOperationException(
                "A specific signed policy/evidence reference (12–500 chars) is required.");
        return normalized;
    }

    private string CalculateHash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), BuildingId.ToString("D"),
            RosterSnapshotId.ToString("D"), RosterSha256,
            ((int)ContractTreatment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)PricingTreatment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)DepositTreatment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)TenantTreatment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ContractPolicyReference, PricingPolicyReference,
            DepositPolicyReference, TenantConsentReference,
            ProposedByUserId.ToString("D"), ProposedAtUtc.ToString("O")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public bool IsExecuted => false;
    public bool IsIndependentlyApproved => false;
}
