using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Contracts;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Append-only, source-database snapshot of the ENTIRE current party roster.
/// It records who is currently party to the existing contract, NOT which
/// tenants have agreed to transfer and NOT a signed transfer approval.
/// Only server-loaded ContractTenant links can be passed by the writer.
/// No PII beyond opaque tenant IDs and role codes.
/// </summary>
public sealed class RoomChangeTenantRosterSnapshotRecord : Entity<Guid>
{
    public Guid RoomChangeRequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid CapturedByUserId { get; private set; }
    public DateTime CapturedAtUtc { get; private set; }
    public int RosterCount { get; private set; }
    public string RosterJson { get; private set; } = string.Empty;
    public string RosterSha256 { get; private set; } = string.Empty;

    private RoomChangeTenantRosterSnapshotRecord() { }
    private RoomChangeTenantRosterSnapshotRecord(Guid id) : base(id) { }

    public static RoomChangeTenantRosterSnapshotRecord Capture(
        Guid id, RoomChangeRequest request,
        IReadOnlyCollection<RoomChangeRosterMember>? members,
        Guid actor, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(members);
        if (id == Guid.Empty || request.Id == Guid.Empty ||
            request.SourceContractId == Guid.Empty ||
            request.SourceRoomId == Guid.Empty ||
            request.BuildingId == Guid.Empty || actor == Guid.Empty ||
            atUtc.Kind != DateTimeKind.Utc ||
            atUtc < request.CreatedAtUtc ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold))
            throw new InvalidOperationException(
                "Only an authenticated, open, scoped request can capture tenant roster.");

        var json = Serialize(request.Id, request.SourceContractId,
            request.SourceRoomId, request.BuildingId, normalized);
        return new RoomChangeTenantRosterSnapshotRecord(id)
        {
            RoomChangeRequestId = request.Id,
            SourceContractId = request.SourceContractId,
            SourceRoomId = request.SourceRoomId,
            BuildingId = request.BuildingId,
            CapturedByUserId = actor,
            CapturedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10)),
            RosterCount = normalized.Length,
            RosterJson = json,
            RosterSha256 = Hash(json)
        };
    }

    public IReadOnlyList<RoomChangeRosterMember> ReadVerifiedRoster()
    {
        if (Id == Guid.Empty || RoomChangeRequestId == Guid.Empty ||
            SourceContractId == Guid.Empty || SourceRoomId == Guid.Empty ||
            BuildingId == Guid.Empty || CapturedByUserId == Guid.Empty ||
            CapturedAtUtc.Kind != DateTimeKind.Utc ||
            RosterSha256.Length != 64 ||
            RosterSha256 != Hash(RosterJson))
            throw new InvalidDataException("Stored transfer tenant roster fingerprint is invalid.");

        try
        {
            using var doc = JsonDocument.Parse(RosterJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 5 ||
                root.GetProperty("requestId").GetString() != RoomChangeRequestId.ToString("D") ||
                root.GetProperty("contractId").GetString() != SourceContractId.ToString("D") ||
                root.GetProperty("sourceRoomId").GetString() != SourceRoomId.ToString("D") ||
                root.GetProperty("buildingId").GetString() != BuildingId.ToString("D"))
                throw new InvalidDataException("Roster scope does not match immutable request.");

            var members = root.GetProperty("tenants").EnumerateArray()
                .Select(x => new RoomChangeRosterMember(
                    Guid.Parse(x.GetProperty("tenantId").GetString()!),
                    (ContractTenantRole)x.GetProperty("role").GetInt32()))
                .ToArray();
            var normalized = Normalize(members);
            if (RosterCount != normalized.Length ||
                RosterJson != Serialize(RoomChangeRequestId, SourceContractId,
                    SourceRoomId, BuildingId, normalized))
                throw new InvalidDataException("Roster is not canonical or has mismatched members.");
            return normalized;
        }
        catch (Exception ex) when (ex is JsonException or
            InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("Stored transfer roster is corrupt.", ex);
        }
    }

    public bool MatchesCurrentRoster(
        Guid requestId, Guid contractId, Guid sourceRoomId, Guid buildingId,
        IReadOnlyCollection<RoomChangeRosterMember> current)
    {
        var archived = ReadVerifiedRoster();
        if (RoomChangeRequestId != requestId ||
            SourceContractId != contractId ||
            SourceRoomId != sourceRoomId ||
            BuildingId != buildingId)
            return false;
        try
        {
            return archived.SequenceEqual(Normalize(current));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static RoomChangeRosterMember[] Normalize(
        IReadOnlyCollection<RoomChangeRosterMember>? members)
    {
        if (members is null || members.Count is < 1 or > 100)
            throw new InvalidOperationException("A bounded current tenant roster is required.");
        var sorted = members.OrderBy(x => x.TenantId).ToArray();
        if (sorted.Any(x => x.TenantId == Guid.Empty ||
                x.Role is not (ContractTenantRole.PrimaryTenant or
                    ContractTenantRole.Member)) ||
            sorted.Select(x => x.TenantId).Distinct().Count() != sorted.Length ||
            sorted.Count(x => x.Role == ContractTenantRole.PrimaryTenant) != 1)
            throw new InvalidOperationException(
                "Current tenant roster must contain unique IDs and one primary tenant.");
        return sorted;
    }

    private static string Serialize(Guid request, Guid contract,
        Guid room, Guid building, RoomChangeRosterMember[] members)
        => JsonSerializer.Serialize(new
        {
            requestId = request.ToString("D"),
            contractId = contract.ToString("D"),
            sourceRoomId = room.ToString("D"),
            buildingId = building.ToString("D"),
            tenants = members.Select(x => new
            {
                tenantId = x.TenantId.ToString("D"), role = (int)x.Role
            }).ToArray()
        });

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}

public sealed record RoomChangeRosterMember(
    Guid TenantId, ContractTenantRole Role);
