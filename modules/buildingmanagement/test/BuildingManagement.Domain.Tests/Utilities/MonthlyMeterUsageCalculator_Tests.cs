using System;
using System.Linq;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Utilities.Tests;

public sealed class MonthlyMeterUsageCalculator_Tests
{
    private static UtilityMeter Meter(Guid roomId, UtilityType type,
        decimal opening = 0m, DateTime? date = null) =>
        new(Guid.NewGuid(), roomId, type,
            "TEST-" + Guid.NewGuid().ToString("N"),
            opening, date ?? new DateTime(2026, 1, 1));

    private static MeterReading Reading(
        UtilityMeter meter, decimal previous, decimal current,
        int month = 10, int day = 19) =>
        new(Guid.NewGuid(), meter.Id, 2026, month, previous, current,
            new DateTime(2026, month, day));

    private static MeterReplacement Replace(
        Guid roomId, UtilityType type, UtilityMeter oldMeter, UtilityMeter newMeter,
        decimal previous, decimal closing, int month = 10, int day = 20) =>
        new(Guid.NewGuid(), oldMeter.Id, newMeter.Id, roomId, type,
            new DateTime(2026, month, day),
            previous, closing, newMeter.InitialReading, "Physical replacement");

    [Fact]
    public void Monthly_Reading_Without_Replacement_Uses_Reading_Delta()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Water, 1000m);
        var reading = Reading(meter, 1000m, 1050m);

        var result = MonthlyMeterUsageCalculator.Calculate(
            room, UtilityType.Water, 2026, 10,
            new[] { meter }, new[] { reading }, Array.Empty<MeterReplacement>());

        Assert.Equal(50m, result.RecordedUsage);
        Assert.Single(result.Segments);
        Assert.Equal(MeterUsageSourceType.MonthlyReading, result.Segments[0].SourceType);
        Assert.Equal(reading.Id, result.Segments[0].SourceId);
    }

    [Fact]
    public void Replacement_During_Period_Adds_Both_Pre_And_Post_Replacement_Usage()
    {
        var room = Guid.NewGuid();
        var oldMeter = Meter(room, UtilityType.Electricity, 1000m);
        var newMeter = Meter(room, UtilityType.Electricity, 15m, new DateTime(2026, 10, 20));
        oldMeter.Deactivate();

        var oldReading = Reading(oldMeter, 1000m, 1050m, day: 19);
        var handover = Replace(room, UtilityType.Electricity, oldMeter, newMeter,
            previous: 1050m, closing: 1100m);
        var newReading = Reading(newMeter, 15m, 20m, day: 31);

        var result = MonthlyMeterUsageCalculator.Calculate(
            room, UtilityType.Electricity, 2026, 10,
            new[] { oldMeter, newMeter },
            new[] { oldReading, newReading },
            new[] { handover });

        // 50 units before the old monthly read + 50 closing + 5 on new meter.
        Assert.Equal(105m, result.RecordedUsage);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(50m, result.Segments.Single(x =>
            x.SourceType == MeterUsageSourceType.ReplacementClosing).Usage);
        Assert.Equal(handover.Id, result.Segments.Single(x =>
            x.SourceType == MeterUsageSourceType.ReplacementClosing).SourceId);
    }

    [Fact]
    public void Multiple_Replacements_In_Same_Period_Keep_Independent_Segments()
    {
        var room = Guid.NewGuid();
        var a = Meter(room, UtilityType.Electricity, 1000m);
        var b = Meter(room, UtilityType.Electricity, 10m, new DateTime(2026, 10, 10));
        var c = Meter(room, UtilityType.Electricity, 2m, new DateTime(2026, 10, 20));
        a.Deactivate();
        b.Deactivate();

        var ab = Replace(room, UtilityType.Electricity, a, b,
            previous: 1000m, closing: 1030m, day: 10);
        var bc = Replace(room, UtilityType.Electricity, b, c,
            previous: 10m, closing: 25m, day: 20);
        var finalReading = Reading(c, 2m, 7m, day: 31);

        var result = MonthlyMeterUsageCalculator.Calculate(
            room, UtilityType.Electricity, 2026, 10,
            new[] { a, b, c }, new[] { finalReading }, new[] { ab, bc });

        Assert.Equal(50m, result.RecordedUsage);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(2, result.Segments.Count(x =>
            x.SourceType == MeterUsageSourceType.ReplacementClosing));
        Assert.Equal(new[] { 30m, 15m, 5m },
            result.Segments.Select(x => x.Usage).ToArray());
    }

    [Fact]
    public void Does_Not_Comingle_Other_Room_Types_Or_Months()
    {
        var room = Guid.NewGuid();
        var otherRoom = Guid.NewGuid();
        var electric = Meter(room, UtilityType.Electricity, 1000m);
        var water = Meter(room, UtilityType.Water, 100m);
        var neighbor = Meter(otherRoom, UtilityType.Electricity, 0m);

        var result = MonthlyMeterUsageCalculator.Calculate(
            room, UtilityType.Electricity, 2026, 10,
            new[] { electric, water, neighbor },
            new[]
            {
                Reading(electric, 1000m, 1012m),
                Reading(electric, 1012m, 1020m, month: 11),
                Reading(water, 100m, 160m),
                Reading(neighbor, 0m, 300m)
            },
            Array.Empty<MeterReplacement>());

        Assert.Equal(12m, result.RecordedUsage);
        Assert.Single(result.Segments);
        Assert.Equal(electric.Id, result.Segments[0].UtilityMeterId);
    }

    [Fact]
    public void Rejects_Duplicate_Monthly_Readings_Rather_Than_Double_Count()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Electricity, 1000m);
        var reading = Reading(meter, 1000m, 1010m);

        Assert.Throws<InvalidOperationException>(() =>
            MonthlyMeterUsageCalculator.Calculate(
                room, UtilityType.Electricity, 2026, 10,
                new[] { meter }, new[] { reading, reading },
                Array.Empty<MeterReplacement>()));
    }

    [Fact]
    public void Empty_Recorded_History_Is_Not_Proof_Of_Zero_Actual_Consumption()
    {
        var room = Guid.NewGuid();
        var active = Meter(room, UtilityType.Water, 1000m);
        var result = MonthlyMeterUsageCalculator.Calculate(
            room, UtilityType.Water, 2026, 10,
            new[] { active },
            Array.Empty<MeterReading>(),
            Array.Empty<MeterReplacement>());

        // This preview only sums posted records; billing readiness is separate.
        Assert.Equal(0m, result.RecordedUsage);
        Assert.Empty(result.Segments);
    }
}
