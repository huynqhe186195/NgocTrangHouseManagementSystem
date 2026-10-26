using System;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class FinalMonthRentPreview_Tests
{
    [Fact]
    public void FINALRENT01_Approved_Uses_Full_Rent_But_Cannot_Issue()
    {
        var (request, contract) = Fixture();
        request.Approve(DateTime.UtcNow, null);
        var result = FinalMonthRentPreview.Assess(request, contract);
        Assert.Equal(3500000m, result.FullMonthRent);
        Assert.Equal(11, result.BillingMonth);
        Assert.Contains("FinalInvoiceNotIssued", result.Blockers);
        Assert.False(result.CanIssue);
    }

    [Fact]
    public void FINALRENT02_Pending_Is_Not_Payable()
    {
        var (request, contract) = Fixture();
        var result = FinalMonthRentPreview.Assess(request, contract);
        Assert.Null(result.FullMonthRent);
        Assert.Contains("MoveOutNotApproved", result.Blockers);
    }

    [Fact]
    public void FINALRENT03_Mismatched_Contract_Is_Rejected()
    {
        var (request, _) = Fixture();
        request.Approve(DateTime.UtcNow, null);
        var other = new Contract(Guid.NewGuid(), "FOREIGN",
            Guid.NewGuid(), new DateTime(2026, 1, 1), null, 5000000m);
        other.Activate();
        var result = FinalMonthRentPreview.Assess(request, other);
        Assert.Null(result.FullMonthRent);
        Assert.Contains("ContractNotEligible", result.Blockers);
    }

    [Fact]
    public void FINALRENT04_Contract_After_Exit_Is_Rejected()
    {
        var (request, _) = Fixture();
        request.Approve(DateTime.UtcNow, null);
        var future = new Contract(request.ContractId, "LATER",
            request.RoomId, new DateTime(2026, 12, 1), null, 3500000m);
        future.Activate();
        Assert.Contains("ContractNotEligible",
            FinalMonthRentPreview.Assess(request, future).Blockers);
    }

    private static (MoveOutRequest, Contract) Fixture()
    {
        var room = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "FINAL-RENT",
            room, new DateTime(2026, 1, 1), null, 3500000m);
        contract.Activate();
        var request = new MoveOutRequest(Guid.NewGuid(), contract.Id,
            room, new DateOnly(2026, 11, 30), "Test");
        return (request, contract);
    }
}
