using System;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Read-only aging of an already committed H4 obligation. A missed top-up
/// deadline is a presentation fact, never a new receivable, fee or penalty:
/// nothing here posts money or alters the signed original obligation.
/// </summary>
public sealed record RoomChangeH4ObligationAging(
    Guid ObligationId,
    RoomChangeH4SettlementKind Kind,
    DateOnly? DueDateVietnam,
    DateOnly AsOfVietnam,
    decimal OriginalAmountVnd,
    decimal SettledAmountVnd,
    decimal OutstandingVnd,
    bool IsOverdue,
    int DaysPastDue);

public static class RoomChangeH4ObligationAgingEvaluator
{
    public static RoomChangeH4ObligationAging Evaluate(
        Guid obligationId,
        RoomChangeH4SettlementKind kind,
        DateOnly? dueDateVietnam,
        DateOnly asOfVietnam,
        decimal originalAmountVnd,
        decimal settledAmountVnd)
    {
        if (obligationId == Guid.Empty ||
            !Enum.IsDefined(kind) ||
            asOfVietnam == default ||
            originalAmountVnd <= 0m ||
            decimal.Truncate(originalAmountVnd) != originalAmountVnd ||
            settledAmountVnd < 0m ||
            settledAmountVnd > originalAmountVnd)
            throw new InvalidOperationException(
                "H4 obligation aging requires a real obligation and a settled amount within it.");

        // Only a deferred top-up carries a collect-by date. A rent credit or
        // a refund liability has no tenant-facing deadline, so it can never
        // be reported as overdue.
        if (kind != RoomChangeH4SettlementKind.DepositTopUp)
        {
            if (dueDateVietnam.HasValue)
                throw new InvalidOperationException(
                    "Only a deferred top-up obligation may carry a due date.");
            return new RoomChangeH4ObligationAging(
                obligationId, kind, null, asOfVietnam, originalAmountVnd,
                settledAmountVnd, originalAmountVnd - settledAmountVnd,
                IsOverdue: false, DaysPastDue: 0);
        }

        if (dueDateVietnam is null || dueDateVietnam == default)
            throw new InvalidOperationException(
                "A deferred top-up obligation must carry its original due date.");

        var outstanding = originalAmountVnd - settledAmountVnd;
        // The due date itself is still on time; lateness begins the day after.
        var overdue = outstanding > 0m && asOfVietnam > dueDateVietnam.Value;
        return new RoomChangeH4ObligationAging(
            obligationId, kind, dueDateVietnam, asOfVietnam,
            originalAmountVnd, settledAmountVnd, outstanding,
            IsOverdue: overdue,
            DaysPastDue: overdue
                ? asOfVietnam.DayNumber - dueDateVietnam.Value.DayNumber
                : 0);
    }
}
