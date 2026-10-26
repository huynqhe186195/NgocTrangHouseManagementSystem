using System;
using BuildingManagement.MoveOuts;
using Xunit;

namespace BuildingManagement.Domain.Tests.MoveOuts;

public sealed class FinalMeterChecklistTask_Tests
{
    [Fact]
    public void METERSIGN01_Can_Complete_Verified_Final_Meter_Task()
    {
        var requestId = Guid.NewGuid();
        var task = new MoveOutChecklistTask(
            Guid.NewGuid(), requestId, MoveOutChecklistTaskType.FinalMeterReading);
        var now = new DateTime(2026, 12, 31, 17, 0, 0, DateTimeKind.Utc);

        task.MarkFinalMeterReadingsVerified(now);

        Assert.True(task.IsCompleted);
        Assert.Equal(now, task.CompletedAt);
        Assert.Equal(requestId, task.MoveOutRequestId);
    }

    [Theory]
    [InlineData(MoveOutChecklistTaskType.RoomInspection)]
    [InlineData(MoveOutChecklistTaskType.FinalInvoice)]
    [InlineData(MoveOutChecklistTaskType.Handover)]
    [InlineData(MoveOutChecklistTaskType.DepositSettlement)]
    public void METERSIGN02_Cannot_Complete_Unrelated_Financial_Or_Handover_Task(
        MoveOutChecklistTaskType taskType)
    {
        var task = new MoveOutChecklistTask(
            Guid.NewGuid(), Guid.NewGuid(), taskType);
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkFinalMeterReadingsVerified(DateTime.UtcNow));
        Assert.False(task.IsCompleted);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void METERSIGN03_Repeated_Verification_Does_Not_Overwrite_Audit_Time()
    {
        var task = new MoveOutChecklistTask(Guid.NewGuid(), Guid.NewGuid(),
            MoveOutChecklistTaskType.FinalMeterReading);
        var verifiedAt = new DateTime(2026, 12, 31, 1, 0, 0, DateTimeKind.Utc);
        task.MarkFinalMeterReadingsVerified(verifiedAt);
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkFinalMeterReadingsVerified(verifiedAt.AddHours(1)));
        Assert.Equal(verifiedAt, task.CompletedAt);
    }

    [Fact]
    public void METERSIGN04_Default_Timestamp_Never_Completes()
    {
        var task = new MoveOutChecklistTask(Guid.NewGuid(), Guid.NewGuid(),
            MoveOutChecklistTaskType.FinalMeterReading);
        Assert.Throws<ArgumentException>(() =>
            task.MarkFinalMeterReadingsVerified(default));
        Assert.False(task.IsCompleted);
    }
}
