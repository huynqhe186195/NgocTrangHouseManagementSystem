using System;
using System.Collections.Generic;

namespace BuildingManagement.Invoices;

/// <summary>
/// A pure, fail-closed boundary for future draft-revision authorization.
/// ALL identifiers and entitled building IDs MUST be resolved on the server
/// after authentication. Never deserialize these facts from a client DTO.
/// This policy is NOT wired into any mutation/API and does not grant access.
/// </summary>
public sealed record InvoiceDraftRevisionAccessFacts(
    Guid? OperatorId,
    bool PermissionGranted,
    Guid RequestedInvoiceId,
    Guid ResolvedInvoiceId,
    Guid InvoiceContractId,
    Guid InvoiceRoomId,
    Guid ContractId,
    Guid ContractRoomId,
    Guid RoomId,
    Guid RoomFloorId,
    Guid FloorId,
    Guid FloorBuildingId,
    Guid BuildingId,
    IReadOnlyCollection<Guid>? AuthorizedBuildingIds,
    InvoiceStatus InvoiceStatus);

public enum InvoiceDraftRevisionAccessStatus
{
    Denied = 0,
    EligibleForDesignOnly = 1
}

public static class InvoiceDraftRevisionAccessPolicy
{
    /// <summary>
    /// Positive outcome is ONLY a pure fact-validation signal, never a
    /// time-stable authorization token. A future service must re-resolve
    /// role membership and relationships inside its locked transaction.
    /// </summary>
    public static InvoiceDraftRevisionAccessStatus Evaluate(
        InvoiceDraftRevisionAccessFacts? facts)
    {
        if (facts is null ||
            !facts.OperatorId.HasValue ||
            facts.OperatorId.Value == Guid.Empty ||
            !facts.PermissionGranted ||
            facts.InvoiceStatus != InvoiceStatus.Draft)
            return InvoiceDraftRevisionAccessStatus.Denied;

        if (facts.RequestedInvoiceId == Guid.Empty ||
            facts.ResolvedInvoiceId == Guid.Empty ||
            facts.InvoiceContractId == Guid.Empty ||
            facts.InvoiceRoomId == Guid.Empty ||
            facts.ContractId == Guid.Empty ||
            facts.ContractRoomId == Guid.Empty ||
            facts.RoomId == Guid.Empty ||
            facts.RoomFloorId == Guid.Empty ||
            facts.FloorId == Guid.Empty ||
            facts.FloorBuildingId == Guid.Empty ||
            facts.BuildingId == Guid.Empty)
            return InvoiceDraftRevisionAccessStatus.Denied;

        if (facts.RequestedInvoiceId != facts.ResolvedInvoiceId ||
            facts.InvoiceContractId != facts.ContractId ||
            facts.InvoiceRoomId != facts.RoomId ||
            facts.ContractRoomId != facts.RoomId ||
            facts.RoomFloorId != facts.FloorId ||
            facts.FloorBuildingId != facts.BuildingId ||
            facts.AuthorizedBuildingIds is null)
            return InvoiceDraftRevisionAccessStatus.Denied;

        var authorized = false;
        foreach (var id in facts.AuthorizedBuildingIds)
        {
            if (id == facts.BuildingId)
            {
                authorized = true;
                break;
            }
        }

        return authorized
            ? InvoiceDraftRevisionAccessStatus.EligibleForDesignOnly
            : InvoiceDraftRevisionAccessStatus.Denied;
    }
}
