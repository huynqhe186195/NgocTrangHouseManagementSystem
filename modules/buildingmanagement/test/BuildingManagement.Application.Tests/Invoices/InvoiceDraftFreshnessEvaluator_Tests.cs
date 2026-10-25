using System;
using System.Collections.Generic;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Invoices;

/// <summary>
/// Pure, database-independent detection of stale persisted utility drafts.
/// These tests are NOT an issuance permission or a financial authorization.
/// </summary>
public sealed class InvoiceDraftFreshnessEvaluator_Tests
{
    [Fact]
    public void INVFRESH01_Identical_Stored_And_Rebuilt_Evidence_Matches()
    {
        var (saved, current) = Baseline();
        Assert.True(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH02_Tariff_Revision_Stales_Stored_Draft()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].UnitPrice = 7000m;
        current.UtilityLines[0].ProvisionalAmount = 70000m;
        current.ProvisionalTotal = 145000m;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH03_Changed_Meter_Counter_Stales_Stored_Draft()
    {
        var (saved, current) = Baseline();
        var line = current.UtilityLines[0];
        line.MeterSegments[0].CurrentReading = 30m;
        line.MeterSegments[0].Usage = 20m;
        line.RecordedQuantity = 20m;
        line.ProvisionalAmount = 70000m;
        current.ProvisionalTotal = 145000m;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH04_Different_Meter_Source_Stales_Even_If_Amount_Unchanged()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments[0].SourceId = Guid.NewGuid();
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH05_Different_Reading_Date_Stales_Even_If_Total_Unchanged()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[0].MeterSegments[0].RecordedDate =
            new DateTime(2026, 10, 30);
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH06_Missing_New_Meter_Closing_Reading_Blocks_Freshness()
    {
        var (saved, current) = Baseline();
        current.Blockers.Add("Electricity:RateOrUsageNotReady");
        current.UtilityLines[0].CoverageIssues.Add("ActiveMeterMissingMonthlyReading");
        current.UtilityLines[0].ProvisionalAmount = null;
        current.ProvisionalTotal = null;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH07_Replacement_Added_Source_Stales_Old_Draft()
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
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH08_Different_Room_Or_Period_Blocks_Freshness()
    {
        var (saved, current) = Baseline();
        current.RoomId = Guid.NewGuid();
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
        current.RoomId = saved.RoomId;
        current.BillingMonth = 11;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH09_Duplicated_Source_Blocks_Freshness()
    {
        var (saved, current) = Baseline();
        current.UtilityLines[1].MeterSegments[0].SourceType =
            current.UtilityLines[0].MeterSegments[0].SourceType;
        current.UtilityLines[1].MeterSegments[0].SourceId =
            current.UtilityLines[0].MeterSegments[0].SourceId;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    [Fact]
    public void INVFRESH10_Inconsistent_Saved_Line_Total_Blocks_Freshness()
    {
        var (saved, current) = Baseline();
        saved.Items[0].ProvisionalAmount = 999m;
        Assert.False(InvoiceDraftFreshnessEvaluator.MatchesCurrentCandidate(saved, current));
    }

    private static (InvoiceDraftDto Saved, InvoiceDraftCandidateDto Current) Baseline()
    {
        var contractId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var electricRate = Guid.NewGuid();
        var waterRate = Guid.NewGuid();
        var electricMeter = Guid.NewGuid();
        var waterMeter = Guid.NewGuid();
        var electricReading = Guid.NewGuid();
        var waterReading = Guid.NewGuid();
        var recordedDate = new DateTime(2026, 10, 31);

        InvoiceDraftSourceDto SavedSource(Guid sourceId, Guid meterId,
            decimal previous, decimal now) => new()
        {
            SourceId = sourceId,
            UtilityMeterId = meterId,
            SourceType = "MonthlyReading",
            RecordedDate = recordedDate,
            PreviousReading = previous,
            CurrentReading = now,
            Quantity = now - previous
        };

        MeterUsageSegmentDto CurrentSource(Guid sourceId, Guid meterId,
            decimal previous, decimal now) => new()
        {
            SourceId = sourceId,
            UtilityMeterId = meterId,
            SourceType = "MonthlyReading",
            RecordedDate = recordedDate,
            PreviousReading = previous,
            CurrentReading = now,
            Usage = now - previous
        };

        var saved = new InvoiceDraftDto
        {
            Id = Guid.NewGuid(),
            ContractId = contractId,
            RoomId = roomId,
            BillingYear = 2026,
            BillingMonth = 10,
            Status = InvoiceStatus.Draft,
            ProvisionalUtilityTotal = 110000m,
            Items = new List<InvoiceDraftItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UtilityType = UtilityType.Electricity,
                    UtilityRateId = electricRate,
                    RateStatus = "SingleRateWholeMonth",
                    Quantity = 10m,
                    UnitPrice = 3500m,
                    ProvisionalAmount = 35000m,
                    Sources = new List<InvoiceDraftSourceDto>
                    {
                        SavedSource(electricReading, electricMeter, 10m, 20m)
                    }
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    UtilityType = UtilityType.Water,
                    UtilityRateId = waterRate,
                    RateStatus = "SingleRateWholeMonth",
                    Quantity = 5m,
                    UnitPrice = 15000m,
                    ProvisionalAmount = 75000m,
                    Sources = new List<InvoiceDraftSourceDto>
                    {
                        SavedSource(waterReading, waterMeter, 100m, 105m)
                    }
                }
            }
        };

        var current = new InvoiceDraftCandidateDto
        {
            ContractId = contractId,
            RoomId = roomId,
            BillingYear = 2026,
            BillingMonth = 10,
            ProvisionalTotal = 110000m,
            UtilityLines = new List<InvoiceDraftUtilityLineCandidateDto>
            {
                new()
                {
                    UtilityType = UtilityType.Electricity,
                    UtilityRateId = electricRate,
                    RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 10m,
                    UnitPrice = 3500m,
                    ProvisionalAmount = 35000m,
                    MeterSegments = new List<MeterUsageSegmentDto>
                    {
                        CurrentSource(electricReading, electricMeter, 10m, 20m)
                    }
                },
                new()
                {
                    UtilityType = UtilityType.Water,
                    UtilityRateId = waterRate,
                    RateStatus = "SingleRateWholeMonth",
                    RecordedQuantity = 5m,
                    UnitPrice = 15000m,
                    ProvisionalAmount = 75000m,
                    MeterSegments = new List<MeterUsageSegmentDto>
                    {
                        CurrentSource(waterReading, waterMeter, 100m, 105m)
                    }
                }
            }
        };
        return (saved, current);
    }
}
