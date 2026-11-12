using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeMidMonthRentReconciliation_Tests
{
    private static RoomChangeDailyRentQuote Quote(DateOnly date,
        RoomChangeTransferDayChargeOwner owner =
            RoomChangeTransferDayChargeOwner.TargetRoom)
        => RoomChangeDailyRentQuoteCalculator.Calculate(
            date, 3_000_000m, 4_000_000m, owner,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            "OWNER-SIGNED-DAY-CUTOFF-AND-ROUNDING-2026");

    private static RoomChangeSourceRentSnapshot Issued(DateOnly on,
        decimal original = 3_000_000m, decimal corrections = 0m,
        decimal receipts = 0m)
        => new(Guid.NewGuid(), on.Year, on.Month, Guid.NewGuid(),
            original, corrections, receipts, new string('a', 64),
            receipts > 0m ? "INDEPENDENT-RECEIPT-ALLOCATION-2026" : null);

    [Fact]
    public void TRF_RENT01_Issued_Source_Rent_Credit_Is_AppendOnly_Candidate()
    {
        var day = new DateOnly(2026, 9, 15);
        var r = RoomChangeMidMonthRentReconciliation.Assess(
            Quote(day), Issued(day, receipts: 500_000m));
        Assert.Equal(1_400_000m, r.ProposedSourceRentVnd);
        Assert.Equal(2_133_333m, r.ProposedTargetRentVnd);
        Assert.Equal(-1_600_000m, r.ProposedSourceRentCorrectionVnd);
        Assert.Equal(900_000m, r.RemainingProposedSourceRentVnd);
        Assert.Equal(0m, r.PotentialSourceOvercollectionVnd);
        Assert.True(r.RequiresAppendOnlyIssuedInvoiceCorrection);
        Assert.False(r.CanPostInvoice);
        Assert.False(r.CanExecuteRoomChange);
        Assert.False(r.IsVerifiedBankPayment);
    }

    [Fact]
    public void TRF_RENT02_Overpaid_Rent_Is_Separate_Refund_Review_Not_AutoPayout()
    {
        var day = new DateOnly(2026, 9, 15);
        var r = RoomChangeMidMonthRentReconciliation.Assess(
            Quote(day), Issued(day, receipts: 2_000_000m));
        Assert.Equal(600_000m, r.PotentialSourceOvercollectionVnd);
        Assert.Equal(0m, r.RemainingProposedSourceRentVnd);
        Assert.True(r.RequiresSeparateOvercollectionReview);
        Assert.False(r.IsVerifiedBankPayment);
    }

    [Fact]
    public void TRF_RENT03_Full_Month_End_Original_Rent_Needs_No_Correction()
    {
        var day = new DateOnly(2026, 11, 30);
        var r = RoomChangeMidMonthRentReconciliation.Assess(
            Quote(day, RoomChangeTransferDayChargeOwner.SourceRoom),
            Issued(day, receipts: 3_000_000m));
        Assert.Equal(0m, r.ProposedSourceRentCorrectionVnd);
        Assert.Equal(0m, r.ProposedTargetRentVnd);
        Assert.False(r.RequiresAppendOnlyIssuedInvoiceCorrection);
    }

    [Fact]
    public void TRF_RENT04_Unissued_First_Day_Has_Zero_Source_Rent()
    {
        var day = new DateOnly(2026, 11, 1);
        var evidence = new RoomChangeSourceRentSnapshot(
            Guid.NewGuid(), day.Year, day.Month, null, 0m, 0m, 0m, null, null);
        var r = RoomChangeMidMonthRentReconciliation.Assess(Quote(day), evidence);
        Assert.Equal(0m, r.ProposedSourceRentVnd);
        Assert.Equal(4_000_000m, r.ProposedTargetRentVnd);
        Assert.Equal(0m, r.ProposedSourceRentCorrectionVnd);
        Assert.False(r.RequiresAppendOnlyIssuedInvoiceCorrection);
    }

    [Fact]
    public void TRF_RENT05_Existing_Approved_Credits_Are_Subtracted_Exactly_Once()
    {
        var day = new DateOnly(2026, 9, 15);
        var r = RoomChangeMidMonthRentReconciliation.Assess(
            Quote(day), Issued(day, corrections: -500_000m,
                receipts: 1_000_000m));
        Assert.Equal(2_500_000m, r.PreviouslyRecognizedSourceRentVnd);
        Assert.Equal(-1_100_000m, r.ProposedSourceRentCorrectionVnd);
        Assert.Equal(400_000m, r.RemainingProposedSourceRentVnd);
    }

    [Fact]
    public void TRF_RENT06_Foreign_Month_Snapshot_Is_Denied()
    {
        var day = new DateOnly(2026, 9, 15);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), Issued(new DateOnly(2026, 10, 15))));
    }

    [Fact]
    public void TRF_RENT07_Unissued_But_Claimed_Receipt_Is_Denied()
    {
        var day = new DateOnly(2026, 11, 1);
        var evidence = new RoomChangeSourceRentSnapshot(
            Guid.NewGuid(), day.Year, day.Month, null, 0m, 0m, 50_000m,
            null, "INDEPENDENT-RECEIPT-ALLOCATION-2026");
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(Quote(day), evidence));
    }

    [Fact]
    public void TRF_RENT08_Receipt_Above_Approved_Source_Balance_Is_Denied()
    {
        var day = new DateOnly(2026, 9, 15);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), Issued(day, corrections: -2_500_000m,
                    receipts: 750_000m)));
    }

    [Fact]
    public void TRF_RENT09_Missing_Invoice_Or_Receipt_Fingerprint_Denied()
    {
        var day = new DateOnly(2026, 9, 15);
        var evidence = Issued(day, receipts: 100_000m);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), evidence with { FrozenInvoiceEvidenceSha256 = "bad" }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), evidence with { ReceiptAllocationEvidenceReference = null }));
    }

    [Fact]
    public void TRF_RENT10_SubVnd_Or_Negative_Original_Charges_Denied()
    {
        var day = new DateOnly(2026, 9, 15);
        var evidence = Issued(day);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), evidence with { OriginalIssuedRentLineVnd = -1m }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(
                Quote(day), evidence with { ApprovedRentCorrectionTotalVnd = 1.5m }));
    }

    [Fact]
    public void TRF_RENT11_Forged_Public_Quote_Arithmetic_Cannot_Enter_Preview()
    {
        var day = new DateOnly(2026, 9, 15);
        var forged = Quote(day) with { ProposedSourceRentVnd = 1m };
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(forged, Issued(day)));
    }

    [Fact]
    public void TRF_RENT12_Empty_Inputs_Cannot_Authorize_Transfer()
    {
        var day = new DateOnly(2026, 9, 15);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(null, Issued(day)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeMidMonthRentReconciliation.Assess(Quote(day), null));
    }
}
