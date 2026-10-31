using System;
using System.Collections.Generic;
using BuildingManagement.Contracts;
using BuildingManagement.Rooms;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Server-sourced, policy-neutral CURRENT eligibility to continue reviewing
/// a recorded request; never an authorization to move a tenant, end a
/// contract, set a room status or dispose of any money.
/// </summary>
public static class RoomChangeRequestReadiness
{
    public static RoomChangeRequestReadinessResult Assess(
        RoomChangeRequestReadinessFacts? facts)
    {
        if (facts is null)
            return new(false, new[] { "CurrentFactsMissing" });

        var blockers = new List<string>();
        if (facts.RequestStatus is not
            (RoomChangeRequestStatus.Pending or RoomChangeRequestStatus.PolicyHold))
            blockers.Add("RequestNoLongerOpen");

        if (facts.RequestId == Guid.Empty ||
            facts.SourceContractId == Guid.Empty ||
            facts.SourceRoomId == Guid.Empty ||
            facts.TargetRoomId == Guid.Empty ||
            facts.BuildingId == Guid.Empty ||
            facts.RequestedDate == default ||
            facts.SourceRoomId == facts.TargetRoomId)
            blockers.Add("RequestIdentityInvalid");

        if (facts.RequestedDate < facts.TodayAtBuilding)
            blockers.Add("RequestedDateElapsed");
        if (facts.SourceContractStatus != ContractStatus.Active)
            blockers.Add("SourceContractNoLongerActive");
        if (facts.SourceRoomStatus != RoomStatus.Occupied)
            blockers.Add("SourceRoomNoLongerOccupied");
        if (facts.SourceBuildingId != facts.BuildingId ||
            facts.TargetBuildingId != facts.BuildingId)
            blockers.Add("BuildingScopeDrift");
        if (facts.TargetRoomStatus != RoomStatus.Available)
            blockers.Add("TargetRoomNoLongerAvailable");
        if (facts.TargetHasPendingOrReservedReservation)
            blockers.Add("TargetReservationNowOpen");
        if (facts.TargetHasSignedOrActiveContract)
            blockers.Add("TargetContractNowCommitted");
        if (facts.SourceHasPendingOrApprovedMoveOut)
            blockers.Add("SourceMoveOutNowOpen");

        // This is deliberately NOT a transfer approval: the separate OI-07
        // contract/pricing/deposit choices and final signed evidence remain
        // mandatory even when no live occupancy conflicts are observed.
        return new(blockers.Count == 0, blockers.ToArray());
    }
}

public sealed record RoomChangeRequestReadinessFacts(
    Guid RequestId, Guid SourceContractId, Guid SourceRoomId,
    Guid TargetRoomId, Guid BuildingId, DateOnly RequestedDate,
    DateOnly TodayAtBuilding, RoomChangeRequestStatus RequestStatus,
    ContractStatus SourceContractStatus, RoomStatus SourceRoomStatus,
    RoomStatus TargetRoomStatus, Guid SourceBuildingId,
    Guid TargetBuildingId, bool TargetHasPendingOrReservedReservation,
    bool TargetHasSignedOrActiveContract, bool SourceHasPendingOrApprovedMoveOut);

public sealed record RoomChangeRequestReadinessResult(
    bool CanContinuePolicyReview, IReadOnlyList<string> Blockers);
