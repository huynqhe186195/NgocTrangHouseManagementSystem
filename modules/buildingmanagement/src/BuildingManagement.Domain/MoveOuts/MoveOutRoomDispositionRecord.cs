using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Audited Phase 2G.5 room handover, committed atomically with the existing
/// 2G.4 deposit decision, money settlements and Contract/Request completion.
/// This records a room outcome, not a second contract or deposit ledger.
/// </summary>
public sealed class MoveOutRoomDispositionRecord : Entity<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid MoveOutRequestId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid CloseoutApprovalId { get; private set; }
    public Guid CompletedByUserId { get; private set; }
    public DateTime CompletedAtUtc { get; private set; }
    public RoomStatus ResultRoomStatus { get; private set; }
    public Guid? FutureReservationId { get; private set; }
    public Guid? FutureSignedContractId { get; private set; }
    public bool RequiresMaintenance { get; private set; }
    public string? MaintenanceReason { get; private set; }
    public string? MaintenanceEvidenceReference { get; private set; }
    public string HandoverEvidenceReference { get; private set; } = string.Empty;
    public string DispositionJson { get; private set; } = string.Empty;
    public string DispositionSha256 { get; private set; } = string.Empty;

    public bool IsDepositRefundInstruction => false;

    private MoveOutRoomDispositionRecord() { }
    private MoveOutRoomDispositionRecord(Guid id) : base(id) { }

    /// <summary>
    /// PendingPayment is NOT confirmed occupancy. Reserved and Signed ARE
    /// committed occupancy. With ambiguous multiple commitments, or a
    /// damaged room with future commitment, require an explicit resolution:
    /// never auto-cancel a tenant's booking or silently release the room.
    /// </summary>
    public static MoveOutRoomDispositionRecord Complete(
        Guid id, Guid contractId, Guid requestId, Guid roomId,
        Guid buildingId, Guid approvalId, Guid actor,
        DateTime completedAtUtc, RoomStatus currentStatus,
        decimal approvedDamageForfeit,
        IReadOnlyCollection<MoveOutRoomReservationFact> reservations,
        IReadOnlyCollection<MoveOutRoomSignedContractFact> signedContracts,
        bool explicitlyRequiresMaintenance, string? maintenanceReason,
        string? maintenanceEvidenceReference, string handoverEvidenceReference)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(signedContracts);
        var booking = reservations.ToArray();
        var signed = signedContracts.ToArray();
        if (id == Guid.Empty || contractId == Guid.Empty ||
            requestId == Guid.Empty || roomId == Guid.Empty ||
            buildingId == Guid.Empty || approvalId == Guid.Empty ||
            actor == Guid.Empty || completedAtUtc.Kind != DateTimeKind.Utc ||
            currentStatus != RoomStatus.Occupied ||
            approvedDamageForfeit < 0m ||
            decimal.Truncate(approvedDamageForfeit) != approvedDamageForfeit ||
            !HasReference(handoverEvidenceReference, 8, 500) ||
            // Only facts observed by the locked handover query are valid.
            // Unexpected states are not silently treated as no future occupant.
            booking.Any(x => x is null || x.Id == Guid.Empty ||
                x.Status is not (RoomReservationStatus.PendingPayment or
                    RoomReservationStatus.Reserved)) ||
            signed.Any(x => x is null || x.Id == Guid.Empty ||
                x.Status != ContractStatus.Signed) ||
            booking.Select(x => x.Id).Distinct().Count() != booking.Length ||
            signed.Select(x => x.Id).Distinct().Count() != signed.Length ||
            signed.Any(x => x.Id == contractId))
            throw new InvalidOperationException("Invalid locked room, booking, actor or handover evidence.");

        var committed = booking.Where(x => x.Status == RoomReservationStatus.Reserved)
            .Select(x => (Id: x.Id, IsReservation: true))
            .Concat(signed.Where(x => x.Status == ContractStatus.Signed)
                .Select(x => (Id: x.Id, IsReservation: false)))
            .ToArray();
        if (committed.Length > 1)
            throw new InvalidOperationException(
                "Conflicting future commitments must be resolved without cancelling booking automatically.");

        var needsRepair = explicitlyRequiresMaintenance || approvedDamageForfeit > 0m;
        if (needsRepair && committed.Length > 0)
            throw new InvalidOperationException(
                "Room maintenance and a confirmed future tenant require a separately approved booking decision.");

        var reason = string.IsNullOrWhiteSpace(maintenanceReason)
            ? null : maintenanceReason.Trim();
        var evidence = string.IsNullOrWhiteSpace(maintenanceEvidenceReference)
            ? null : maintenanceEvidenceReference.Trim();
        if ((needsRepair && (!HasReference(reason, 8, 500) ||
                              !HasReference(evidence, 8, 500))) ||
            (!needsRepair && (reason is not null || evidence is not null)))
            throw new InvalidOperationException(
                "Maintenance handover requires signed repair reason/evidence; clean room cannot claim damage.");

        var status = needsRepair ? RoomStatus.Maintenance :
            committed.Length == 1 ? RoomStatus.Reserved : RoomStatus.Available;
        var futureReservation = committed.Length == 1 && committed[0].IsReservation
            ? committed[0].Id : (Guid?)null;
        var futureContract = committed.Length == 1 && !committed[0].IsReservation
            ? committed[0].Id : (Guid?)null;
        var at = completedAtUtc.AddTicks(-(completedAtUtc.Ticks % 10L));
        var json = JsonSerializer.Serialize(new
        {
            ContractId = contractId, MoveOutRequestId = requestId,
            RoomId = roomId, BuildingId = buildingId,
            CloseoutApprovalId = approvalId, CompletedByUserId = actor,
            CompletedAtUtc = at, ResultRoomStatus = status,
            FutureReservationId = futureReservation,
            FutureSignedContractId = futureContract,
            RequiresMaintenance = needsRepair, MaintenanceReason = reason,
            MaintenanceEvidenceReference = evidence,
            HandoverEvidenceReference = handoverEvidenceReference.Trim()
        });
        var result = new MoveOutRoomDispositionRecord(id)
        {
            ContractId = contractId, MoveOutRequestId = requestId,
            RoomId = roomId, BuildingId = buildingId,
            CloseoutApprovalId = approvalId, CompletedByUserId = actor,
            CompletedAtUtc = at, ResultRoomStatus = status,
            FutureReservationId = futureReservation,
            FutureSignedContractId = futureContract,
            RequiresMaintenance = needsRepair, MaintenanceReason = reason,
            MaintenanceEvidenceReference = evidence,
            HandoverEvidenceReference = handoverEvidenceReference.Trim(),
            DispositionJson = json,
            DispositionSha256 = Sha(json)
        };
        result.VerifyStoredDisposition();
        return result;
    }

    public void VerifyStoredDisposition()
    {
        if (Id == Guid.Empty || ContractId == Guid.Empty ||
            MoveOutRequestId == Guid.Empty || RoomId == Guid.Empty ||
            BuildingId == Guid.Empty || CloseoutApprovalId == Guid.Empty ||
            CompletedByUserId == Guid.Empty ||
            CompletedAtUtc.Kind != DateTimeKind.Utc ||
            ResultRoomStatus is not (RoomStatus.Available or
                RoomStatus.Reserved or RoomStatus.Maintenance) ||
            (ResultRoomStatus == RoomStatus.Reserved) !=
                (FutureReservationId.HasValue || FutureSignedContractId.HasValue) ||
            (FutureReservationId.HasValue && FutureSignedContractId.HasValue) ||
            (RequiresMaintenance != (ResultRoomStatus == RoomStatus.Maintenance)) ||
            (RequiresMaintenance &&
                (!HasReference(MaintenanceReason, 8, 500) ||
                 !HasReference(MaintenanceEvidenceReference, 8, 500))) ||
            (!RequiresMaintenance &&
                (MaintenanceReason is not null || MaintenanceEvidenceReference is not null)) ||
            !HasReference(HandoverEvidenceReference, 8, 500) ||
            string.IsNullOrEmpty(DispositionJson) ||
            DispositionJson.Length > 1000000 ||
            DispositionSha256 != Sha(DispositionJson))
            throw new InvalidDataException("Immutable move-out room disposition does not match proof.");

        try
        {
            using var json = JsonDocument.Parse(DispositionJson);
            var o = json.RootElement;
            if (o.GetProperty("ContractId").GetGuid() != ContractId ||
                o.GetProperty("MoveOutRequestId").GetGuid() != MoveOutRequestId ||
                o.GetProperty("RoomId").GetGuid() != RoomId ||
                o.GetProperty("BuildingId").GetGuid() != BuildingId ||
                o.GetProperty("CloseoutApprovalId").GetGuid() != CloseoutApprovalId ||
                o.GetProperty("CompletedByUserId").GetGuid() != CompletedByUserId ||
                o.GetProperty("CompletedAtUtc").GetDateTime() != CompletedAtUtc ||
                o.GetProperty("ResultRoomStatus").GetInt32() != (int)ResultRoomStatus ||
                ReadGuid(o, "FutureReservationId") != FutureReservationId ||
                ReadGuid(o, "FutureSignedContractId") != FutureSignedContractId ||
                o.GetProperty("RequiresMaintenance").GetBoolean() != RequiresMaintenance ||
                ReadText(o, "MaintenanceReason") != MaintenanceReason ||
                ReadText(o, "MaintenanceEvidenceReference") != MaintenanceEvidenceReference ||
                o.GetProperty("HandoverEvidenceReference").GetString() !=
                    HandoverEvidenceReference)
                throw new InvalidDataException("Move-out room disposition archive has changed.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Room disposition JSON cannot be verified.", ex);
        }
    }

    private static string? ReadText(JsonElement o, string key)
    {
        var v = o.GetProperty(key);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
    }

    private static Guid? ReadGuid(JsonElement o, string key)
    {
        var v = o.GetProperty(key);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetGuid();
    }

    private static bool HasReference(string? v, int min, int max)
        => !string.IsNullOrWhiteSpace(v) &&
            v.Trim().Length >= min && v.Trim().Length <= max;
    private static string Sha(string s) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

public sealed record MoveOutRoomReservationFact(
    Guid Id, RoomReservationStatus Status);
public sealed record MoveOutRoomSignedContractFact(
    Guid Id, ContractStatus Status);
