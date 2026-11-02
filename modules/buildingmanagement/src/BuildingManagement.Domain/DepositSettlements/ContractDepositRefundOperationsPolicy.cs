using System;
using System.Collections.Generic;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// OI-04 / D04 / D07: 7 business days after reconciliation/Owner approval,
/// never after merely submitting a notice. Holidays are supplied by the
/// authorized Vietnam calendar; Saturday and Sunday never count.
/// No bank claim can be treated as an executed refund without the separate
/// immutable independent-statement review.
/// </summary>
public static class ContractDepositRefundOperationsPolicy
{
    public const int RefundDeadlineBusinessDays = 7;

    public static DateOnly DueOn(DateOnly reconciledAndApprovedOn,
        IReadOnlyCollection<DateOnly> statutoryHolidays)
    {
        if (reconciledAndApprovedOn == default ||
            statutoryHolidays is null)
            throw new ArgumentException("A dated approved holiday calendar is required.");
        var holidays = new HashSet<DateOnly>(statutoryHolidays);
        var cursor = reconciledAndApprovedOn;
        var days = 0;
        while (days < RefundDeadlineBusinessDays)
        {
            cursor = cursor.AddDays(1);
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
                holidays.Contains(cursor))
                continue;
            days++;
        }
        return cursor;
    }

    public static bool ValidRecipient(string? beneficiaryName, string? bank,
        string? accountNumber, string? ownerSignedReference)
        => beneficiaryName?.Trim().Length is >= 3 and <= 200 &&
           bank?.Trim().Length is >= 3 and <= 100 &&
           accountNumber?.Trim().Length is >= 6 and <= 40 &&
           ownerSignedReference?.Trim().Length is >= 12 and <= 500 &&
           !string.Equals(accountNumber?.Trim(), ownerSignedReference?.Trim(),
               StringComparison.Ordinal);

    public static ContractDepositRefundOperatingStatus Status(
        decimal approvedRefundVnd, bool recipientApproved, bool independentlyReviewed,
        bool hasFailedAttempt, DateOnly? dueOn, DateOnly asOf)
    {
        if (approvedRefundVnd < 0m ||
            decimal.Truncate(approvedRefundVnd) != approvedRefundVnd ||
            asOf == default)
            throw new ArgumentException("Invalid refund status inputs.");
        if (approvedRefundVnd == 0m) return ContractDepositRefundOperatingStatus.NotRequired;
        if (independentlyReviewed) return ContractDepositRefundOperatingStatus.RefundVerified;
        if (!recipientApproved) return ContractDepositRefundOperatingStatus.AwaitingRecipientApproval;
        if (hasFailedAttempt) return ContractDepositRefundOperatingStatus.TransferFailed;
        if (dueOn is null) throw new ArgumentException("Approved refund missing due date.");
        return asOf > dueOn
            ? ContractDepositRefundOperatingStatus.Overdue
            : ContractDepositRefundOperatingStatus.AwaitingManualTransferAndReview;
    }
}

public enum ContractDepositRefundOperatingStatus
{
    NotRequired = 0,
    AwaitingRecipientApproval = 1,
    AwaitingManualTransferAndReview = 2,
    TransferFailed = 3,
    Overdue = 4,
    RefundVerified = 5
}
