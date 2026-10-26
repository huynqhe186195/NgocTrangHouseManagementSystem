using System;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class FinalMoveOutBillReview_Tests
{
    [Fact]
    public void FINALBILL01_Approved_Quote_Combines_Full_Rent_And_Both_Draft_Utilities()
    {
        var (request, contract, draft) = Fixture();
        var review = Review(request, contract, draft);

        Assert.Equal(3_500_000m, review.FullMonthRent);
        Assert.Equal(30_000m, review.ElectricityAmount);
        Assert.Equal(20_000m, review.WaterAmount);
        Assert.Equal(3_550_000m, review.ProvisionalRentAndUtilities);
        Assert.Equal(draft.Id, review.UtilityDraftId);
        Assert.Contains("FinalInvoiceNotIssued", review.Blockers);
        Assert.Contains("OutstandingDebtNotReconciled", review.Blockers);
        Assert.Contains("DepositSettlementNotVerified", review.Blockers);
        Assert.False(review.CanIssue);
        Assert.False(review.IsReceivablePosted);
    }

    [Fact]
    public void FINALBILL02_Missing_Or_Ambiguous_Draft_Does_Not_Invent_Utility_Money()
    {
        var (request, contract, draft) = Fixture();
        var missing = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, Array.Empty<Invoice>(),
            Array.Empty<InvoiceItem>(), Array.Empty<InvoiceItemMeterSource>());
        Assert.Null(missing.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftMissing", missing.Blockers);

        var duplicate = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft, draft },
            draft.Items.ToArray(), draft.Items.SelectMany(x => x.MeterSources).ToArray());
        Assert.Null(duplicate.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftAmbiguous", duplicate.Blockers);
    }

    [Fact]
    public void FINALBILL03_Absent_Meter_Source_Refuses_Money_Quote()
    {
        var (request, contract, draft) = Fixture();
        var result = FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft }, draft.Items.ToArray(),
            Array.Empty<InvoiceItemMeterSource>());
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftEvidenceInvalid", result.Blockers);
    }

    [Fact]
    public void FINALBILL04_Pending_Request_Does_Not_Quote()
    {
        var (request, contract, draft) = Fixture(approve: false);
        var result = Review(request, contract, draft);
        Assert.Null(result.FullMonthRent);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("MoveOutNotApproved", result.Blockers);
    }

    [Fact]
    public void FINALBILL05_Another_Contract_Cannot_Borrow_Utility_Draft()
    {
        var (request, contract, _) = Fixture();
        var foreign = BuildDraft(Guid.NewGuid(), request.RoomId);
        var result = Review(request, contract, foreign);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftScopeMismatch", result.Blockers);
    }

    [Fact]
    public void FINALBILL06_Old_Month_Draft_Cannot_Be_Used_For_Final_Month()
    {
        var (request, contract, _) = Fixture();
        var stale = BuildDraft(contract.Id, request.RoomId, month: 10);
        var result = Review(request, contract, stale);
        Assert.Null(result.ProvisionalRentAndUtilities);
        Assert.Contains("FinalUtilityDraftScopeMismatch", result.Blockers);
    }

    private static FinalMoveOutBillReview Review(
        MoveOutRequest request, Contract contract, Invoice draft)
        => FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft }, draft.Items.ToArray(),
            draft.Items.SelectMany(x => x.MeterSources).ToArray());

    private static (MoveOutRequest, Contract, Invoice) Fixture(bool approve = true)
    {
        var roomId = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "TEST-FINAL",
            roomId, new DateTime(2026, 1, 1), null, 3_500_000m);
        contract.Activate();
        var request = new MoveOutRequest(Guid.NewGuid(), contract.Id,
            roomId, new DateOnly(2026, 11, 30), null);
        if (approve)
            request.Approve(DateTime.UtcNow, null);
        return (request, contract, BuildDraft(contract.Id, roomId));
    }

    private static Invoice BuildDraft(Guid contractId, Guid roomId, int month = 11)
    {
        var sourceType = Enum.GetValues<MeterUsageSourceType>()[0];
        InvoiceDraftUtilityLineSnapshot Line(
            UtilityType type, decimal quantity, decimal unitPrice)
        {
            var meter = new InvoiceDraftMeterSegmentSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), sourceType,
                new DateTime(2026, month, 30), 0m, quantity);
            return new InvoiceDraftUtilityLineSnapshot(
                type, Guid.NewGuid(), "SingleRateWholeMonth",
                quantity, unitPrice, quantity * unitPrice,
                new[] { meter }, Array.Empty<string>());
        }

        var snapshot = InvoiceDraftSnapshot.Capture(
            contractId, roomId, 2026, month,
            new[]
            {
                Line(UtilityType.Electricity, 10m, 3_000m),
                Line(UtilityType.Water, 2m, 10_000m)
            });
        return Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
    }
}
