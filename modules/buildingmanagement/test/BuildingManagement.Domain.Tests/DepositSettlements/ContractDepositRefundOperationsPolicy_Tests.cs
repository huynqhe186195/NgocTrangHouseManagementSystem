using System;
using BuildingManagement.DepositSettlements;
using Xunit;

namespace BuildingManagement.DepositSettlements;

public sealed class ContractDepositRefundOperationsPolicy_Tests
{
    [Fact]
    public void OI04OPS01_Seven_Business_Days_Excludes_Weekend()
    {
        // Monday 2026-11-02 => Wednesday 2026-11-11.
        Assert.Equal(new DateOnly(2026, 11, 11),
            ContractDepositRefundOperationsPolicy.DueOn(
                new DateOnly(2026, 11, 2), Array.Empty<DateOnly>()));
    }

    [Fact]
    public void OI04OPS02_Statutory_Holiday_Not_Counted()
    {
        Assert.Equal(new DateOnly(2026, 11, 12),
            ContractDepositRefundOperationsPolicy.DueOn(
                new DateOnly(2026, 11, 2),
                new[] { new DateOnly(2026, 11, 5) }));
    }

    [Fact]
    public void OI04OPS03_Approval_On_Friday_Excludes_Weekend()
    {
        Assert.Equal(new DateOnly(2026, 11, 10),
            ContractDepositRefundOperationsPolicy.DueOn(
                new DateOnly(2026, 10, 30), Array.Empty<DateOnly>()));
    }

    [Fact]
    public void OI04OPS04_Beneficiary_Needs_Owner_Signed_Destination()
    {
        Assert.True(ContractDepositRefundOperationsPolicy.ValidRecipient(
            "Nguyen Van A", "Domestic Bank", "9876543210",
            "OWNER-SIGNED-PAYEE-2026-001"));
        Assert.False(ContractDepositRefundOperationsPolicy.ValidRecipient(
            "Nguyen Van A", "Domestic Bank", "123",
            "OWNER-SIGNED-PAYEE-2026-001"));
        Assert.False(ContractDepositRefundOperationsPolicy.ValidRecipient(
            "Nguyen Van A", "Domestic Bank", "9876543210", null));
    }

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 2)]
    [InlineData(true, false, true, 3)]
    [InlineData(true, true, true, 5)]
    public void OI04OPS05_Status_Is_Never_Refunded_Without_Independent_Review(
        bool approved, bool reviewed, bool failed, int expected)
    {
        var status = ContractDepositRefundOperationsPolicy.Status(
            1_000_000m, approved, reviewed, failed,
            new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 3));
        Assert.Equal((ContractDepositRefundOperatingStatus)expected, status);
    }

    [Fact]
    public void OI04OPS06_Overdue_Does_Not_Mark_Refund_As_Executed()
    {
        Assert.Equal(ContractDepositRefundOperatingStatus.Overdue,
            ContractDepositRefundOperationsPolicy.Status(1_000_000m, true,
                false, false, new DateOnly(2026, 11, 11),
                new DateOnly(2026, 11, 12)));
        Assert.Equal(ContractDepositRefundOperatingStatus.NotRequired,
            ContractDepositRefundOperationsPolicy.Status(0m, false,
                false, false, null, new DateOnly(2026, 11, 12)));
    }
}
