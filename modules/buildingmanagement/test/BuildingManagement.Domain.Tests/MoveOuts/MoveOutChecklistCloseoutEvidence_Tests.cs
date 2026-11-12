using System;
using BuildingManagement.MoveOuts;
using Xunit;

namespace BuildingManagement.DomainTests.MoveOuts;

public sealed class MoveOutChecklistCloseoutEvidence_Tests
{
    [Theory]
    [InlineData(MoveOutChecklistTaskType.RoomInspection)]
    [InlineData(MoveOutChecklistTaskType.FinalInvoice)]
    [InlineData(MoveOutChecklistTaskType.Handover)]
    [InlineData(MoveOutChecklistTaskType.DepositSettlement)]
    public void NonMeter_Closeout_Requires_Explicit_Actor_Proof_And_One_Time_Signoff(
        MoveOutChecklistTaskType kind)
    {
        var task = new MoveOutChecklistTask(
            Guid.NewGuid(), Guid.NewGuid(), kind);
        var at = DateTime.UtcNow;
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkCloseoutEvidenceVerified(kind, Guid.Empty,
                "SIGNED-HANDOVER-01", at));
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkCloseoutEvidenceVerified(kind, Guid.NewGuid(),
                "missing", at));
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkCloseoutEvidenceVerified(
                MoveOutChecklistTaskType.FinalMeterReading,
                Guid.NewGuid(), "SIGNED-HANDOVER-01", at));
        Assert.False(task.IsCompleted);

        task.MarkCloseoutEvidenceVerified(kind, Guid.NewGuid(),
            "SIGNED-HANDOVER-01", at);
        Assert.True(task.IsCompleted);
        Assert.Equal(at, task.CompletedAt);
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkCloseoutEvidenceVerified(kind, Guid.NewGuid(),
                "REPLAYED-SIGNOFF-01", at));
    }

    [Fact]
    public void Meter_Checklist_Rejects_Standalone_Financial_Closeout_Signoff()
    {
        var task = new MoveOutChecklistTask(Guid.NewGuid(), Guid.NewGuid(),
            MoveOutChecklistTaskType.FinalMeterReading);
        Assert.Throws<InvalidOperationException>(() =>
            task.MarkCloseoutEvidenceVerified(
                MoveOutChecklistTaskType.FinalMeterReading,
                Guid.NewGuid(), "SIGNED-HANDOVER-01", DateTime.UtcNow));
        Assert.False(task.IsCompleted);
    }
}
