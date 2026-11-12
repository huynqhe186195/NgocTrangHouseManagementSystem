using System;
using System.Collections.Generic;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// Pure Phase 2G.3A.7 draft correction PREVIEW tests.
/// No invoice, meter, payment or production data is modified.
/// </summary>
public sealed class InvoiceDraftRebuildPreflight_Tests
{
    private const string Explanation = "Meter reading corrected after verification";

    [Fact]
    public void REBUILD01_Unchanged_Evidence_Does_Not_Rebuild()
    {
        var (saved, current) = Baseline();
        Assert.Equal(InvoiceDraftRebuildGateStatus.AlreadyCurrent,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD02_Stale_Tariff_With_Valid_Reason_Is_Preflight_Ready()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].UnitPrice = 7000m;
        current.UtilityLines[0].ProvisionalAmount = 70000m;
        current.ProvisionalTotal = 145000m;

        var result = InvoiceDraftRebuildPreflight.Assess(
            saved, current, "  Tariff corrected after audit  ");
        Assert.True(result.CanPrepareRevision);
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign, result.Status);
        Assert.Equal(145000m, result.ProposedUtilityTotal);
        Assert.Equal("Tariff corrected after audit", result.NormalizedReason);
        // No mutation of old or new financial values.
        Assert.Equal(110000m, saved.ProvisionalUtilityTotal);
        Assert.Equal(3500m, saved.Items[0].UnitPrice);
    }

    [Fact]
    public void REBUILD03_Stale_Draft_Needs_Meaningful_Reason()
    {
        var (saved, current) = StaleRate();
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReasonRequired,
            InvoiceDraftRebuildPreflight.Assess(saved, current, null).Status);
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReasonRequired,
            InvoiceDraftRebuildPreflight.Assess(saved, current, "short").Status);
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReasonRequired,
            InvoiceDraftRebuildPreflight.Assess(saved, current,
                new string('r', 501)).Status);
    }

    [Fact]
    public void REBUILD04_Cross_Room_Or_Contract_Scope_Is_Rejected()
    {
        var (saved, current) = StaleRate();
        current.RoomId = Guid.NewGuid();
        Assert.Equal(InvoiceDraftRebuildGateStatus.DifferentBillingScope,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
        current.RoomId = saved.RoomId;
        current.ContractId = Guid.NewGuid();
        Assert.Equal(InvoiceDraftRebuildGateStatus.DifferentBillingScope,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD05_Unready_Candidate_Cannot_Be_Treated_As_Zero()
    {
        var (saved, current) = Baseline();
        current.Blockers.Add("Electricity:RateOrUsageNotReady");
        current.UtilityLines[0].CoverageIssues.Add("ActiveMeterMissingMonthlyReading");
        current.UtilityLines[0].ProvisionalAmount = null;
        current.ProvisionalTotal = null;
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD06_Old_Draft_With_Tampered_Total_Cannot_Be_Revised()
    {
        var (saved, current) = StaleRate();
        saved.ProvisionalUtilityTotal = 999999m;
        Assert.Equal(InvoiceDraftRebuildGateStatus.SavedDraftInvalid,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD07_Only_Existing_Draft_Status_Is_Eligible()
    {
        var (saved, current) = StaleRate();
        saved.Status = (InvoiceStatus)99;
        Assert.Equal(InvoiceDraftRebuildGateStatus.SavedDraftInvalid,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD08_Changed_Source_Identity_With_Equal_Total_Is_Recognized()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments[0].SourceId = Guid.NewGuid();
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD09_Candidate_Duplicate_Global_Source_Is_Rejected()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[1].MeterSegments[0].SourceId =
            current.UtilityLines[0].MeterSegments[0].SourceId;
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD10_Counter_Quantity_Inconsistency_Is_Rejected()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments[0].CurrentReading = 40m;
        // Attempts to keep the old money/quantity despite a changed counter.
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD11_Unknown_Source_Type_Is_Rejected()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments[0].SourceType = "UnverifiedImport";
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD12_New_Source_With_Verified_Consumption_Can_Be_Proposed()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments.Add(new MeterUsageSegmentDto
        {
            SourceId = Guid.NewGuid(),
            UtilityMeterId = current.UtilityLines[0].MeterSegments[0].UtilityMeterId,
            SourceType = "ReplacementClosing",
            RecordedDate = new DateTime(2026, 10, 31),
            PreviousReading = 20m,
            CurrentReading = 25m,
            Usage = 5m
        });
        current.UtilityLines[0].RecordedQuantity = 15m;
        current.UtilityLines[0].ProvisionalAmount = 52500m;
        current.ProvisionalTotal = 127500m;
        Assert.Equal(InvoiceDraftRebuildGateStatus.ReadyForRevisionDesign,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD13_Saved_Duplicate_Source_Must_Not_Be_Silently_Overwritten()
    {
        var (saved, current) = StaleRate();
        saved.Items[1].Sources[0].SourceId = saved.Items[0].Sources[0].SourceId;
        Assert.Equal(InvoiceDraftRebuildGateStatus.SavedDraftInvalid,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    [Fact]
    public void REBUILD14_Unsupported_Candidate_Price_Cannot_Bypass_Source_Guards()
    {
        var (saved, current) = StaleRate();
        current.UtilityLines[0].UnitPrice = -1m;
        current.UtilityLines[0].ProvisionalAmount = -10m;
        current.ProvisionalTotal = 74990m;
        Assert.Equal(InvoiceDraftRebuildGateStatus.CurrentCandidateNotReady,
            InvoiceDraftRebuildPreflight.Assess(saved, current, Explanation).Status);
    }

    private static (InvoiceDraftDto Saved, InvoiceDraftCandidateDto Current)
        StaleRate()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].UnitPrice = 7000m;
        current.UtilityLines[0].ProvisionalAmount = 70000m;
        current.ProvisionalTotal = 145000m;
        return (saved, current);
    }

    private static (InvoiceDraftDto Saved, InvoiceDraftCandidateDto Current)
        Baseline()
    {
        var contract = Guid.NewGuid();
        var room = Guid.NewGuid();
        var rateElectric = Guid.NewGuid();
        var rateWater = Guid.NewGuid();
        var meterElectric = Guid.NewGuid();
        var meterWater = Guid.NewGuid();
        var readingElectric = Guid.NewGuid();
        var readingWater = Guid.NewGuid();
        var date = new DateTime(2026, 10, 31);

        InvoiceDraftSourceDto Stored(Guid source, Guid meter,
            decimal previous, decimal current) => new()
        {
            SourceId = source, UtilityMeterId = meter,
            SourceType = "MonthlyReading", RecordedDate = date,
            PreviousReading = previous, CurrentReading = current,
            Quantity = current - previous
        };

        MeterUsageSegmentDto Live(Guid source, Guid meter,
            decimal previous, decimal current) => new()
        {
            SourceId = source, UtilityMeterId = meter,
            SourceType = "MonthlyReading", RecordedDate = date,
            PreviousReading = previous, CurrentReading = current,
            Usage = current - previous
        };

        var saved = new InvoiceDraftDto
        {
            Id = Guid.NewGuid(), ContractId = contract, RoomId = room,
            BillingYear = 2026, BillingMonth = 10,
            Status = InvoiceStatus.Draft, ProvisionalUtilityTotal = 110000m,
            Items = new List<InvoiceDraftItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), UtilityType = UtilityType.Electricity,
                    UtilityRateId = rateElectric, RateStatus = "SingleRateWholeMonth",
                    Quantity = 10m, UnitPrice = 3500m, ProvisionalAmount = 35000m,
                    Sources = new()
                    {
                        Stored(readingElectric, meterElectric, 10m, 20m)
                    }
                },
                new()
                {
                    Id = Guid.NewGuid(), UtilityType = UtilityType.Water,
                    UtilityRateId = rateWater, RateStatus = "SingleRateWholeMonth",
                    Quantity = 5m, UnitPrice = 15000m, ProvisionalAmount = 75000m,
                    Sources = new()
                    {
                        Stored(readingWater, meterWater, 100m, 105m)
                    }
                }
            }
        };

        var candidate = new InvoiceDraftCandidateDto
        {
            ContractId = contract, RoomId = room,
            BillingYear = 2026, BillingMonth = 10,
            ProvisionalTotal = 110000m,
            UtilityLines = new()
            {
                new()
                {
                    UtilityType = UtilityType.Electricity,
                    UtilityRateId = rateElectric, RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 10m, UnitPrice = 3500m,
                    ProvisionalAmount = 35000m,
                    MeterSegments = new()
                    {
                        Live(readingElectric, meterElectric, 10m, 20m)
                    }
                },
                new()
                {
                    UtilityType = UtilityType.Water,
                    UtilityRateId = rateWater, RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 5m, UnitPrice = 15000m,
                    ProvisionalAmount = 75000m,
                    MeterSegments = new()
                    {
                        Live(readingWater, meterWater, 100m, 105m)
                    }
                }
            }
        };

        return (saved, candidate);
    }
}
