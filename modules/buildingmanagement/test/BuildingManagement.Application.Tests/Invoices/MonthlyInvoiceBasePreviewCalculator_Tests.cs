using System;
using System.Collections.Generic;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Application.Tests.Invoices;

public sealed class MonthlyInvoiceBasePreviewCalculator_Tests
{
    private static InvoiceDraftCandidateDto Candidate()
    {
        return new InvoiceDraftCandidateDto
        {
            ContractId = Guid.NewGuid(),
            RoomId = Guid.NewGuid(),
            BillingYear = 2026,
            BillingMonth = 9,
            ProvisionalTotal = 83_000m,
            UtilityLines = new List<InvoiceDraftUtilityLineCandidateDto>
            {
                new()
                {
                    UtilityType = UtilityType.Electricity,
                    UtilityRateId = Guid.NewGuid(),
                    RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 10m,
                    UnitPrice = 3_500m,
                    ProvisionalAmount = 35_000m,
                    MeterSegments = new List<MeterUsageSegmentDto> { new() }
                },
                new()
                {
                    UtilityType = UtilityType.Water,
                    UtilityRateId = Guid.NewGuid(),
                    RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 4m,
                    UnitPrice = 12_000m,
                    ProvisionalAmount = 48_000m,
                    MeterSegments = new List<MeterUsageSegmentDto> { new() }
                }
            }
        };
    }

    [Fact]
    public void INV_MONTH01_KnownFullRentAndVerifiedUtilities_CanPreviewBase_NotIssue()
    {
        var candidate = Candidate();
        var report = MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, 3_000_000m);

        Assert.Equal(candidate.ContractId, report.ContractId);
        Assert.Equal(candidate.RoomId, report.RoomId);
        Assert.Equal(3_000_000m, report.KnownFullMonthRentAmount);
        Assert.Equal(35_000m, report.ProvisionalElectricityAmount);
        Assert.Equal(48_000m, report.ProvisionalWaterAmount);
        Assert.Equal(3_083_000m, report.ProvisionalRentAndUtilitiesAmount);
        Assert.Null(report.OtherServiceFeesAmount);
        Assert.False(report.OtherServiceFeesReviewed);
        Assert.False(report.CanIssue);
        Assert.False(report.IsOfficialReceivable);
        Assert.Contains("OtherServiceFeesRequireReviewBeforeIssuance", report.Blockers);
    }

    [Fact]
    public void INV_MONTH02_MissingMonthlyReading_NeverQuotesAnInventedAmount()
    {
        var candidate = Candidate();
        candidate.Blockers.Add("Electricity:RateOrUsageNotReady");
        candidate.UtilityLines[0].CoverageIssues.Add("MissingEarlierReading");
        candidate.UtilityLines[0].ProvisionalAmount = null;
        candidate.ProvisionalTotal = null;

        var report = MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, 3_000_000m);
        Assert.Null(report.ProvisionalRentAndUtilitiesAmount);
        Assert.Equal(3_000_000m, report.KnownFullMonthRentAmount);
        Assert.Contains("MonthlyUtilitiesNotFullyVerified", report.Blockers);
        Assert.False(report.CanIssue);
    }

    [Fact]
    public void INV_MONTH03_StaleAggregateOrUnknownUtility_IsRejected()
    {
        var candidate = Candidate();
        candidate.ProvisionalTotal = 80_000m;
        var stale = MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, 3_000_000m);
        Assert.Null(stale.ProvisionalRentAndUtilitiesAmount);
        Assert.Contains("MonthlyUtilitiesNotFullyVerified", stale.Blockers);

        candidate = Candidate();
        candidate.UtilityLines[1].UtilityType = UtilityType.Electricity;
        var duplicate = MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, 3_000_000m);
        Assert.Null(duplicate.ProvisionalRentAndUtilitiesAmount);
        Assert.Contains("BothUtilityLinesRequired", duplicate.Blockers);
    }

    [Fact]
    public void INV_MONTH04_FractionalVnd_IsNotSilentlyRounded()
    {
        var candidate = Candidate();
        candidate.UtilityLines[0].UnitPrice = 3500.01m;
        candidate.UtilityLines[0].ProvisionalAmount = 35000.1m;
        candidate.ProvisionalTotal = 83000.1m;

        var report = MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, 3_000_000m);
        Assert.Null(report.ProvisionalRentAndUtilitiesAmount);
        Assert.Contains("UnsafeOrUnroundedUtilityLine", report.Blockers);
        Assert.False(report.CanIssue);
    }
}
