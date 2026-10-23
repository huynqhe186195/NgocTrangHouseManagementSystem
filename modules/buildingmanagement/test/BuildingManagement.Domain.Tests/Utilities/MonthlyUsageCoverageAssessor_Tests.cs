using System;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Utilities.Tests;

public sealed class MonthlyUsageCoverageAssessor_Tests
{
    private static UtilityMeter NewMeter(Guid room) =>
        new(Guid.NewGuid(), room, UtilityType.Electricity,
            Guid.NewGuid().ToString("N"), 100m, new DateTime(2026, 1, 1));

    private static MeterReading Reading(UtilityMeter meter, int month,
        int day, decimal current) =>
        new(Guid.NewGuid(), meter.Id, 2026, month, 100m,
            current, new DateTime(2026, month, day));

    [Fact]
    public void Early_Month_Read_Is_Not_Proof_Of_Period_End()
    {
        var room = Guid.NewGuid();
        var meter = NewMeter(room);
        var issues = MonthlyUsageCoverageAssessor.Assess(
            room, UtilityType.Electricity, 2026, 10,
            new[] { meter },
            new[] { Reading(meter, 9, 30, 100m), Reading(meter, 10, 5, 110m) },
            Array.Empty<MeterReplacement>());

        Assert.Contains("PeriodEndReadingNotVerified", issues);
    }

    [Fact]
    public void Manual_Deactivation_Needs_Review_Even_After_Month_End_Read()
    {
        var room = Guid.NewGuid();
        var meter = NewMeter(room);
        meter.Deactivate();

        var issues = MonthlyUsageCoverageAssessor.Assess(
            room, UtilityType.Electricity, 2026, 10,
            new[] { meter },
            new[] { Reading(meter, 9, 30, 100m), Reading(meter, 10, 31, 110m) },
            Array.Empty<MeterReplacement>());

        Assert.Contains("ManualMeterDeactivationRequiresReview", issues);
        Assert.DoesNotContain("PeriodEndReadingNotVerified", issues);
    }
}
