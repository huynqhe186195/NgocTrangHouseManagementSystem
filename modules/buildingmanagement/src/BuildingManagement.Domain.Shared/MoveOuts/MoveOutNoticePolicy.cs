using System;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// The notice window is one calendar month from the SUBMISSION date;
/// an actual move-out date must also be a month-end date.
/// Business dates use the Vietnam calendar (UTC+7), not UTC midnight.
/// </summary>
public static class MoveOutNoticePolicy
{
    private static readonly TimeZoneInfo VietnamTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public static DateOnly VietnamDate(DateTime now)
    {
        var utc = now.Kind switch
        {
            DateTimeKind.Utc => now,
            DateTimeKind.Local => now.ToUniversalTime(),
            _ => DateTime.SpecifyKind(now, DateTimeKind.Utc)
        };

        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(utc, VietnamTimeZone));
    }

    public static DateOnly EarliestPermittedMoveOutDate(DateOnly submittedOn)
    {
        var oneCalendarMonthLater = submittedOn.AddMonths(1);
        var lastDay = DateTime.DaysInMonth(
            oneCalendarMonthLater.Year, oneCalendarMonthLater.Month);

        return new DateOnly(oneCalendarMonthLater.Year,
            oneCalendarMonthLater.Month, lastDay);
    }

    public static bool IsMonthEnd(DateOnly date) =>
        date.Day == DateTime.DaysInMonth(date.Year, date.Month);

    public static bool IsValid(DateOnly submittedOn, DateOnly moveOutOn) =>
        IsMonthEnd(moveOutOn) &&
        moveOutOn >= EarliestPermittedMoveOutDate(submittedOn);
}
