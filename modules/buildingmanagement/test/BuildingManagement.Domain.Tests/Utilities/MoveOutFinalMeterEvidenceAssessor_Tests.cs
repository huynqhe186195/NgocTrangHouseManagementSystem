using System;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Utilities.Tests;

public sealed class MoveOutFinalMeterEvidenceAssessor_Tests
{
    private static readonly DateOnly MoveOutDate = new(2026, 11, 30);

    private static UtilityMeter Meter(Guid roomId, UtilityType type,
        DateTime? installed = null, decimal opening = 0m) =>
        new(Guid.NewGuid(), roomId, type, Guid.NewGuid().ToString("N"),
            opening, installed ?? new DateTime(2026, 1, 1));

    private static MeterReading Read(UtilityMeter meter, DateTime date,
        decimal oldReading = 0m, decimal currentReading = 10m) =>
        new(Guid.NewGuid(), meter.Id, 2026, 11, oldReading,
            currentReading, date);

    private static MeterReplacement Handover(
        Guid roomId, UtilityMeter oldMeter, UtilityMeter newMeter, DateTime date) =>
        new(Guid.NewGuid(), oldMeter.Id, newMeter.Id, roomId,
            oldMeter.UtilityType, date, oldMeter.InitialReading,
            oldMeter.InitialReading + 20m, newMeter.InitialReading, "Changed");

    [Fact]
    public void Same_Day_Read_On_Responsible_Meter_Is_Evidence()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Electricity);
        var reading = Read(meter, new DateTime(2026, 11, 30));
        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Electricity, MoveOutDate,
            new[] { meter }, new[] { reading }, Array.Empty<MeterReplacement>(),
            Array.Empty<string>());

        Assert.True(result.HasValidReadingEvidence);
        Assert.Equal(reading.Id, result.MeterReadingId);
        Assert.Equal(10m, result.FinalReading);
    }

    [Fact]
    public void Reading_Before_MoveOut_Is_Not_Proof_Of_Final_Index()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Water);
        var reading = Read(meter, new DateTime(2026, 11, 29));
        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Water, MoveOutDate,
            new[] { meter }, new[] { reading }, Array.Empty<MeterReplacement>(),
            Array.Empty<string>());

        Assert.Equal("FinalReadingMissingOrWrongDate", result.Status);
        Assert.False(result.HasValidReadingEvidence);
    }

    [Fact]
    public void Replacement_Before_MoveOut_Must_Read_New_Physical_Meter()
    {
        var room = Guid.NewGuid();
        var first = Meter(room, UtilityType.Electricity);
        var second = Meter(room, UtilityType.Electricity,
            new DateTime(2026, 11, 15), opening: 2m);
        first.Deactivate();
        var replacement = Handover(room, first, second, new DateTime(2026, 11, 15));

        var oldFinalRead = Read(first, new DateTime(2026, 11, 30));
        var newFinalRead = Read(second, new DateTime(2026, 11, 30), 2m, 17m);

        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Electricity, MoveOutDate,
            new[] { first, second }, new[] { oldFinalRead, newFinalRead },
            new[] { replacement }, Array.Empty<string>());

        Assert.True(result.HasValidReadingEvidence);
        Assert.Equal(second.Id, result.UtilityMeterId);
        Assert.Equal(newFinalRead.Id, result.MeterReadingId);
    }

    [Fact]
    public void Future_Replacement_Does_Not_Remove_Historical_Meter()
    {
        var room = Guid.NewGuid();
        var first = Meter(room, UtilityType.Water);
        var next = Meter(room, UtilityType.Water,
            new DateTime(2026, 12, 5));
        first.Deactivate();
        var replacement = Handover(room, first, next, new DateTime(2026, 12, 5));
        var reading = Read(first, new DateTime(2026, 11, 30));

        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Water, MoveOutDate,
            new[] { first, next }, new[] { reading },
            new[] { replacement }, Array.Empty<string>());

        Assert.True(result.HasValidReadingEvidence);
        Assert.Equal(first.Id, result.UtilityMeterId);
    }

    [Fact]
    public void Manually_Deactivated_Meter_Needs_Lifecycle_Review()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Electricity);
        meter.Deactivate();
        var reading = Read(meter, new DateTime(2026, 11, 30));

        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Electricity, MoveOutDate,
            new[] { meter }, new[] { reading },
            Array.Empty<MeterReplacement>(), Array.Empty<string>());

        Assert.Equal("MeterLifecycleRequiresReview", result.Status);
    }

    [Fact]
    public void Existing_Final_Read_Does_Not_Override_Usage_Coverage_Warnings()
    {
        var room = Guid.NewGuid();
        var meter = Meter(room, UtilityType.Water);
        var reading = Read(meter, new DateTime(2026, 11, 30));

        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Water, MoveOutDate,
            new[] { meter }, new[] { reading },
            Array.Empty<MeterReplacement>(),
            new[] { "MissingPreviousMonthReading" });

        Assert.Equal("UsageCoverageRequiresReview", result.Status);
        Assert.False(result.HasValidReadingEvidence);
        Assert.Equal(reading.Id, result.MeterReadingId);
    }

    [Fact]
    public void Multiple_Possible_Physical_Meters_Is_Ambiguous()
    {
        var room = Guid.NewGuid();
        var a = Meter(room, UtilityType.Electricity);
        var b = Meter(room, UtilityType.Electricity);
        var result = MoveOutFinalMeterEvidenceAssessor.Assess(
            room, UtilityType.Electricity, MoveOutDate,
            new[] { a, b }, Array.Empty<MeterReading>(),
            Array.Empty<MeterReplacement>(), Array.Empty<string>());

        Assert.Equal("AmbiguousMeterAtMoveOutDate", result.Status);
        Assert.False(result.HasValidReadingEvidence);
    }
}
