namespace BuildingManagement.RoomChanges;

// Intent and review only. There is deliberately NO Approved/Completed
// state until OI-07 transfer contract, pricing and deposit policy is signed.
public enum RoomChangeRequestStatus
{
    Pending = 0,
    PolicyHold = 1,
    Rejected = 2,
    Cancelled = 3,
    Completed = 4
}
