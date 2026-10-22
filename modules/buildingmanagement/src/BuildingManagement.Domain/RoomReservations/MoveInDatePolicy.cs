using System;

namespace BuildingManagement.RoomReservations;

/// <summary>
/// A date-only reservation window in the business timezone (Vietnam).
/// Both bounds are inclusive.
/// </summary>
public sealed record MoveInDateWindow(DateOnly Earliest, DateOnly Latest)
{
    public bool Contains(DateOnly selectedDate) =>
        selectedDate >= Earliest && selectedDate <= Latest;
}

/// <summary>
/// A snapshot of availability facts retrieved by the application service.
/// It MUST be refreshed under the appropriate DB lock when confirming a reservation/payment.
/// </summary>
public sealed record MoveInDateContext(
    DateOnly TodayInVietnam,
    bool IsRoomOperational,
    bool IsPhysicallyVacant,
    bool HasActiveContract,
    DateOnly? ActiveContractEndDate,
    bool HasSignedContract,
    bool HasConfirmedReservation,
    bool HasUnexpiredRenewalHold);

public enum MoveInDateRejectionReason
{
    None = 0,
    RoomNotOperational = 1,
    ConfirmedReservationExists = 2,
    SignedContractExists = 3,
    RenewalPriorityHoldExists = 4,
    RoomStatusAndContractInconsistent = 5,
    ActiveContractStillExists = 6,
    OccupantContractHasNoEndDate = 7,
    OccupantContractOverlapsNextMonth = 8,
    SelectedDateOutsideWindow = 9
}

public sealed record MoveInDateAvailability(
    MoveInDateWindow? Window,
    MoveInDateRejectionReason RejectionReason)
{
    public bool CanReserve =>
        Window is not null && RejectionReason == MoveInDateRejectionReason.None;
}

/// <summary>
/// Business policy approved for Phase 2F.1A.
///  - Physically vacant room: today through the cutoff of next month.
///  - Occupied room: the 1st through cutoff of next month, only if the
///    current active contract ends BEFORE the 1st of next month.
///  - The cutoff is the first Sunday that falls ON OR AFTER day 7.
/// This policy does not query the database, approve payments or confirm move-in.
/// </summary>
public static class MoveInDatePolicy
{
    public static MoveInDateAvailability GetAvailability(MoveInDateContext context)
    {
        if (!context.IsRoomOperational)
        {
            return Reject(MoveInDateRejectionReason.RoomNotOperational);
        }

        if (context.HasConfirmedReservation)
        {
            return Reject(MoveInDateRejectionReason.ConfirmedReservationExists);
        }

        if (context.HasSignedContract)
        {
            return Reject(MoveInDateRejectionReason.SignedContractExists);
        }

        if (context.HasUnexpiredRenewalHold)
        {
            return Reject(MoveInDateRejectionReason.RenewalPriorityHoldExists);
        }

        var firstDayOfNextMonth =
            new DateOnly(context.TodayInVietnam.Year, context.TodayInVietnam.Month, 1)
                .AddMonths(1);

        var cutoff = GetNextMonthCutoff(context.TodayInVietnam);

        if (context.IsPhysicallyVacant)
        {
            // A room that is physically empty is not legally available
            // while an active contract still exists.
            if (context.HasActiveContract)
            {
                return Reject(MoveInDateRejectionReason.ActiveContractStillExists);
            }

            return Allow(new MoveInDateWindow(context.TodayInVietnam, cutoff));
        }

        if (!context.HasActiveContract)
        {
            // Do not interpret an Occupied status without a corresponding
            // active contract as proof that the room will be handed over.
            return Reject(MoveInDateRejectionReason.RoomStatusAndContractInconsistent);
        }

        if (context.ActiveContractEndDate is null)
        {
            return Reject(MoveInDateRejectionReason.OccupantContractHasNoEndDate);
        }

        if (context.ActiveContractEndDate.Value >= firstDayOfNextMonth)
        {
            // EndDate is inclusive: an end date on the 1st is a conflict.
            return Reject(MoveInDateRejectionReason.OccupantContractOverlapsNextMonth);
        }

        return Allow(new MoveInDateWindow(firstDayOfNextMonth, cutoff));
    }

    public static MoveInDateAvailability ValidateSelection(
        MoveInDateContext context,
        DateOnly selectedDate)
    {
        var availability = GetAvailability(context);

        if (!availability.CanReserve)
        {
            return availability;
        }

        if (!availability.Window!.Contains(selectedDate))
        {
            // Retain the window so the UI can explain valid dates.
            return new MoveInDateAvailability(
                availability.Window,
                MoveInDateRejectionReason.SelectedDateOutsideWindow);
        }

        return availability;
    }

    public static DateOnly GetNextMonthCutoff(DateOnly todayInVietnam)
    {
        var firstDayOfNextMonth =
            new DateOnly(todayInVietnam.Year, todayInVietnam.Month, 1)
                .AddMonths(1);

        // Explicit business definition: first Sunday on/after the 7th.
        var seventh = firstDayOfNextMonth.AddDays(6);
        var daysUntilSunday =
            ((int)DayOfWeek.Sunday - (int)seventh.DayOfWeek + 7) % 7;

        return seventh.AddDays(daysUntilSunday);
    }

    private static MoveInDateAvailability Allow(MoveInDateWindow window) =>
        new(window, MoveInDateRejectionReason.None);

    private static MoveInDateAvailability Reject(MoveInDateRejectionReason reason) =>
        new(null, reason);
}
