using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeH4ObligationAging_Tests
{
    private static readonly DateOnly Due = new(2026, 12, 15);

    private static RoomChangeH4ObligationAging Evaluate(
        RoomChangeH4SettlementKind kind = RoomChangeH4SettlementKind.DepositTopUp,
        DateOnly? asOf = null,
        decimal original = 1_000_000m,
        decimal settled = 0m)
        => RoomChangeH4ObligationAgingEvaluator.Evaluate(
            Guid.NewGuid(), kind, Due,
            asOf ?? Due, original, settled);

    private static RoomChangeH4ObligationAging EvaluateUndated(
        RoomChangeH4SettlementKind kind,
        DateOnly? dueDate,
        DateOnly asOf)
        => RoomChangeH4ObligationAgingEvaluator.Evaluate(
            Guid.NewGuid(), kind, dueDate, asOf, 1_000_000m, 0m);

    [Fact]
    public void H4AGE01_Due_Today_Is_Not_Overdue()
    {
        var a = Evaluate();
        Assert.False(a.IsOverdue);
        Assert.Equal(0, a.DaysPastDue);
        Assert.Equal(1_000_000m, a.OutstandingVnd);
    }

    [Fact]
    public void H4AGE02_Day_After_Due_Is_One_Day_Overdue()
    {
        var a = Evaluate(asOf: Due.AddDays(1));
        Assert.True(a.IsOverdue);
        Assert.Equal(1, a.DaysPastDue);
    }

    [Fact]
    public void H4AGE03_Day_Before_Due_Is_Not_Overdue()
    {
        var a = Evaluate(asOf: Due.AddDays(-1));
        Assert.False(a.IsOverdue);
        Assert.Equal(0, a.DaysPastDue);
    }

    [Fact]
    public void H4AGE04_Fully_Settled_Past_Due_Is_Not_Overdue()
    {
        var a = Evaluate(asOf: Due.AddDays(30), settled: 1_000_000m);
        Assert.False(a.IsOverdue);
        Assert.Equal(0, a.DaysPastDue);
        Assert.Equal(0m, a.OutstandingVnd);
    }

    [Fact]
    public void H4AGE05_Partial_Installment_Ages_Only_The_Remainder()
    {
        var a = Evaluate(asOf: Due.AddDays(10), settled: 400_000m);
        Assert.True(a.IsOverdue);
        Assert.Equal(10, a.DaysPastDue);
        Assert.Equal(600_000m, a.OutstandingVnd);
        Assert.Equal(1_000_000m, a.OriginalAmountVnd);
        Assert.Equal(400_000m, a.SettledAmountVnd);
    }

    [Fact]
    public void H4AGE06_Rent_Credit_Has_No_Deadline_And_Cannot_Age()
    {
        var a = EvaluateUndated(
            RoomChangeH4SettlementKind.NextMonthIssuedRentCredit, null,
            Due.AddYears(1));
        Assert.Null(a.DueDateVietnam);
        Assert.False(a.IsOverdue);
        Assert.Equal(0, a.DaysPastDue);
    }

    [Fact]
    public void H4AGE07_Refund_Liability_Has_No_Deadline_And_Cannot_Age()
    {
        var a = EvaluateUndated(
            RoomChangeH4SettlementKind.IndependentBankRefund, null,
            Due.AddYears(1));
        Assert.Null(a.DueDateVietnam);
        Assert.False(a.IsOverdue);
        Assert.Equal(0, a.DaysPastDue);
    }

    [Fact]
    public void H4AGE08_TopUp_Without_Due_Date_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => EvaluateUndated(
            RoomChangeH4SettlementKind.DepositTopUp, null, Due));
    }

    [Fact]
    public void H4AGE09_Non_TopUp_Carrying_A_Due_Date_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => EvaluateUndated(
            RoomChangeH4SettlementKind.IndependentBankRefund, Due, Due));
    }

    [Fact]
    public void H4AGE10_Zero_Or_Negative_Original_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() => Evaluate(original: 0m));
        Assert.Throws<InvalidOperationException>(() => Evaluate(original: -1m));
    }

    [Fact]
    public void H4AGE11_Settled_Above_Original_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(original: 1_000_000m, settled: 1_000_001m));
    }

    [Fact]
    public void H4AGE12_Fractional_Amount_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(original: 1_000_000.5m));
    }

    [Fact]
    public void H4AGE13_Empty_Obligation_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeH4ObligationAgingEvaluator.Evaluate(
                Guid.Empty, RoomChangeH4SettlementKind.DepositTopUp, Due,
                Due, 1_000_000m, 0m));
    }

    [Fact]
    public void H4AGE14_Undefined_Kind_Is_Refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeH4ObligationAgingEvaluator.Evaluate(
                Guid.NewGuid(), (RoomChangeH4SettlementKind)9, Due,
                Due, 1_000_000m, 0m));
    }
}
