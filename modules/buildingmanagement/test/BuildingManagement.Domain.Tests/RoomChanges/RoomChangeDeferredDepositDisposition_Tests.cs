using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeDeferredDepositDisposition_Tests
{
    private static RoomChangeDepositDifferenceFacts Facts(
        decimal required = 3_000_000m, decimal held = 3_000_000m,
        decimal deductions = 0m)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), held, deductions, required,
            "CASH-BACKED-SOURCE-LEDGER-REVIEW-01",
            "SIGNED-TARGET-DEPOSIT-PRICE-REFERENCE-01",
            deductions > 0m ? "SIGNED-OWNER-DEDUCTION-REVIEW-01" : null,
            HasUnresolvedDispute: false,
            HasEarlierRefundOrCarryForward: false);

    private static RoomChangeDeferredDepositDispositionPlan Plan(
        RoomChangeDepositDifferenceFacts facts,
        DateOnly? on = null,
        RoomChangeDepositSurplusChoice choice =
            RoomChangeDepositSurplusChoice.None,
        DateOnly? due = null)
        => RoomChangeDeferredDepositDispositionPlanner.Plan(
            Guid.NewGuid(), facts, on ?? new DateOnly(2026, 11, 15),
            choice, due);

    [Fact]
    public void TRF_H4DEF01_Higher_Cash_Need_Does_Not_Pretend_Collected()
    {
        var p = Plan(Facts(required: 4_000_000m));
        Assert.Equal(3_000_000m, p.CarriedTargetDepositVnd);
        Assert.Equal(1_000_000m, p.OutstandingTopUpReceivableVnd);
        Assert.Equal(new DateOnly(2026, 12, 15),
            p.OutstandingTopUpDueDateVietnam!.Value);
        Assert.True(p.BusinessAllowsTransferWithUnpaidTopUp);
        Assert.False(p.IsBankCashVerified);
        Assert.False(p.IsFinancialJournalPosted);
        Assert.False(p.CanExecuteRoomChange);
    }

    [Fact]
    public void TRF_H4DEF02_One_Calendar_Month_Jan31_Clamps_To_Feb28()
    {
        var p = Plan(Facts(required: 4_000_000m),
            on: new DateOnly(2027, 1, 31));
        Assert.Equal(new DateOnly(2027, 2, 28),
            p.OutstandingTopUpDueDateVietnam!.Value);
    }

    [Fact]
    public void TRF_H4DEF03_Leap_February_Maximum_Is_Feb29()
    {
        var p = Plan(Facts(required: 4_000_000m),
            on: new DateOnly(2028, 1, 31));
        Assert.Equal(new DateOnly(2028, 2, 29),
            p.OutstandingTopUpDueDateVietnam!.Value);
    }

    [Fact]
    public void TRF_H4DEF04_Earlier_Due_Date_Is_Allowed()
    {
        var p = Plan(Facts(required: 4_000_000m),
            due: new DateOnly(2026, 12, 1));
        Assert.Equal(new DateOnly(2026, 12, 1),
            p.OutstandingTopUpDueDateVietnam!.Value);
    }

    [Fact]
    public void TRF_H4DEF05_Due_Later_Than_One_Month_Or_Before_Transfer_Denied()
    {
        var facts = Facts(required: 4_000_000m);
        Assert.Throws<InvalidOperationException>(() =>
            Plan(facts, due: new DateOnly(2026, 12, 16)));
        Assert.Throws<InvalidOperationException>(() =>
            Plan(facts, due: new DateOnly(2026, 11, 14)));
    }

    [Fact]
    public void TRF_H4DEF06_Equal_Deposit_Needs_No_New_Money()
    {
        var p = Plan(Facts());
        Assert.Null(p.OutstandingTopUpDueDateVietnam);
        Assert.Equal(0m, p.OutstandingTopUpReceivableVnd);
        Assert.Equal(0m, p.NextMonthRentCreditLiabilityVnd);
        Assert.Equal(0m, p.ManualRefundLiabilityVnd);
        Assert.False(p.BusinessAllowsTransferWithUnpaidTopUp);
    }

    [Fact]
    public void TRF_H4DEF07_Lower_Deposit_Credits_Next_Month_Rent_Not_Cash()
    {
        var p = Plan(Facts(required: 2_000_000m),
            choice: RoomChangeDepositSurplusChoice.CreditNextMonthRent);
        Assert.Equal(2_000_000m, p.CarriedTargetDepositVnd);
        Assert.Equal(1_000_000m, p.NextMonthRentCreditLiabilityVnd);
        Assert.Equal(0m, p.ManualRefundLiabilityVnd);
        Assert.False(p.IsRentCreditApplied);
        Assert.False(p.IsRefundPaid);
    }

    [Fact]
    public void TRF_H4DEF08_Lower_Deposit_May_Choose_Independent_Refund()
    {
        var p = Plan(Facts(required: 2_000_000m),
            choice: RoomChangeDepositSurplusChoice.ReviewedManualRefund);
        Assert.Equal(0m, p.NextMonthRentCreditLiabilityVnd);
        Assert.Equal(1_000_000m, p.ManualRefundLiabilityVnd);
        Assert.False(p.IsRefundPaid);
        Assert.False(p.IsBankCashVerified);
    }

    [Fact]
    public void TRF_H4DEF09_Surplus_Without_Exclusive_Owner_Choice_Denied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Plan(Facts(required: 2_000_000m)));
    }

    [Fact]
    public void TRF_H4DEF10_Topup_Or_Equal_Cannot_Invent_Refund_Or_Due()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Plan(Facts(required: 4_000_000m),
                choice: RoomChangeDepositSurplusChoice.ReviewedManualRefund));
        Assert.Throws<InvalidOperationException>(() =>
            Plan(Facts(), due: new DateOnly(2026, 12, 15)));
    }

    [Fact]
    public void TRF_H4DEF11_Posted_Owner_Deduction_Adjusts_Only_Real_Surplus()
    {
        var p = Plan(Facts(required: 2_000_000m,
            deductions: 500_000m),
            choice: RoomChangeDepositSurplusChoice.CreditNextMonthRent);
        Assert.Equal(500_000m, p.AlreadyPostedApprovedDeductionsVnd);
        Assert.Equal(2_000_000m, p.CarriedTargetDepositVnd);
        Assert.Equal(500_000m, p.NextMonthRentCreditLiabilityVnd);
    }

    [Fact]
    public void TRF_H4DEF12_Disputes_Replay_And_Unknown_Choice_Fail_Closed()
    {
        var facts = Facts(required: 4_000_000m);
        Assert.Throws<InvalidOperationException>(() =>
            Plan(facts with { HasUnresolvedDispute = true }));
        Assert.Throws<InvalidOperationException>(() =>
            Plan(facts with { HasEarlierRefundOrCarryForward = true }));
        Assert.Throws<InvalidOperationException>(() =>
            Plan(facts, choice: (RoomChangeDepositSurplusChoice)999));
    }
    [Fact]
    public void TRF_H4DEF13_MonthEnd_Due_Clamps_Across_April_And_December()
    {
        var facts = Facts(required: 4_000_000m);
        var april = Plan(facts, on: new DateOnly(2026, 4, 30));
        Assert.Equal(new DateOnly(2026, 5, 30),
            april.OutstandingTopUpDueDateVietnam!.Value);

        var december = Plan(facts, on: new DateOnly(2026, 12, 31));
        Assert.Equal(new DateOnly(2027, 1, 31),
            december.OutstandingTopUpDueDateVietnam!.Value);
    }

}
