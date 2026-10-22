using System;
using Xunit;

namespace BuildingManagement.RoomReservations;

public class MoveInDatePolicy_Tests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    [Fact]
    public void Vacant_room_window_starts_today_and_ends_on_november_8()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant());

        Assert.True(result.CanReserve);
        Assert.Equal(Today, result.Window!.Earliest);
        Assert.Equal(new DateOnly(2026, 11, 8), result.Window.Latest);
    }

    [Fact]
    public void Occupied_room_with_contract_ending_october_31_can_be_reserved_for_early_november()
    {
        var result = MoveInDatePolicy.GetAvailability(Occupied(new DateOnly(2026, 10, 31)));

        Assert.True(result.CanReserve);
        Assert.Equal(new DateOnly(2026, 11, 1), result.Window!.Earliest);
        Assert.Equal(new DateOnly(2026, 11, 8), result.Window.Latest);
    }

    [Theory]
    [InlineData(2026, 11, 1)]
    [InlineData(2026, 11, 15)]
    [InlineData(2026, 12, 1)]
    public void Occupant_contract_ending_in_or_after_november_blocks_the_window(
        int year, int month, int day)
    {
        var result = MoveInDatePolicy.GetAvailability(Occupied(new DateOnly(year, month, day)));

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.OccupantContractOverlapsNextMonth,
            result.RejectionReason);
    }

    [Fact]
    public void Occupied_room_with_no_contract_end_date_is_not_reservable()
    {
        var result = MoveInDatePolicy.GetAvailability(Occupied(null));

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.OccupantContractHasNoEndDate,
            result.RejectionReason);
    }

    [Fact]
    public void Occupied_status_without_active_contract_is_not_reservable()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            IsPhysicallyVacant = false
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.RoomStatusAndContractInconsistent,
            result.RejectionReason);
    }

    [Fact]
    public void Physically_vacant_room_with_active_contract_is_not_reservable()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            HasActiveContract = true,
            ActiveContractEndDate = new DateOnly(2026, 10, 31)
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.ActiveContractStillExists,
            result.RejectionReason);
    }

    [Fact]
    public void Room_under_maintenance_or_inactive_is_blocked()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            IsRoomOperational = false
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.RoomNotOperational, result.RejectionReason);
    }

    [Fact]
    public void Existing_paid_reservation_blocks_a_second_reservation()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            HasConfirmedReservation = true
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.ConfirmedReservationExists,
            result.RejectionReason);
    }

    [Fact]
    public void Signed_contract_blocks_the_room()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            HasSignedContract = true
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.SignedContractExists, result.RejectionReason);
    }

    [Fact]
    public void An_unexpired_renewal_priority_hold_blocks_unpaid_reservation()
    {
        var result = MoveInDatePolicy.GetAvailability(Vacant() with
        {
            HasUnexpiredRenewalHold = true
        });

        Assert.False(result.CanReserve);
        Assert.Equal(MoveInDateRejectionReason.RenewalPriorityHoldExists,
            result.RejectionReason);
    }

    [Theory]
    [InlineData(2026, 10, 22, true)]
    [InlineData(2026, 11, 8, true)]
    [InlineData(2026, 10, 21, false)]
    [InlineData(2026, 11, 9, false)]
    public void Selection_must_fit_vacant_room_window(
        int year, int month, int day, bool expectedAllowed)
    {
        var result = MoveInDatePolicy.ValidateSelection(Vacant(), new DateOnly(year, month, day));

        Assert.Equal(expectedAllowed, result.CanReserve);
        if (!expectedAllowed)
        {
            Assert.Equal(MoveInDateRejectionReason.SelectedDateOutsideWindow,
                result.RejectionReason);
        }
    }

    [Theory]
    [InlineData(2026, 11, 1, true)]
    [InlineData(2026, 11, 8, true)]
    [InlineData(2026, 10, 31, false)]
    [InlineData(2026, 11, 9, false)]
    public void Selection_must_fit_occupied_room_window(
        int year, int month, int day, bool expectedAllowed)
    {
        var result = MoveInDatePolicy.ValidateSelection(
            Occupied(new DateOnly(2026, 10, 31)),
            new DateOnly(year, month, day));

        Assert.Equal(expectedAllowed, result.CanReserve);
    }

    [Theory]
    [InlineData(2026, 10, 22, 2026, 11, 8)]
    [InlineData(2026, 11, 5, 2026, 12, 13)]
    [InlineData(2026, 12, 3, 2027, 1, 10)]
    [InlineData(2027, 1, 20, 2027, 2, 7)]
    public void Cutoff_is_first_sunday_on_or_after_seventh_of_next_month(
        int todayYear, int todayMonth, int todayDay,
        int cutoffYear, int cutoffMonth, int cutoffDay)
    {
        var actual = MoveInDatePolicy.GetNextMonthCutoff(
            new DateOnly(todayYear, todayMonth, todayDay));

        Assert.Equal(new DateOnly(cutoffYear, cutoffMonth, cutoffDay), actual);
    }

    private static MoveInDateContext Vacant() => new(
        TodayInVietnam: Today,
        IsRoomOperational: true,
        IsPhysicallyVacant: true,
        HasActiveContract: false,
        ActiveContractEndDate: null,
        HasSignedContract: false,
        HasConfirmedReservation: false,
        HasUnexpiredRenewalHold: false);

    private static MoveInDateContext Occupied(DateOnly? contractEnd) =>
        Vacant() with
        {
            IsPhysicallyVacant = false,
            HasActiveContract = true,
            ActiveContractEndDate = contractEnd
        };
}
