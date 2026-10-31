using System;
using System.IO;
using BuildingManagement.Contracts;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeHandoverEvidence_Tests
{
    private static readonly DateTime Now =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static (RoomChangeRequest Request, UtilityMeter E, MeterReading Er,
        UtilityMeter W, MeterReading Wr) Fixture()
    {
        var room = Guid.NewGuid();
        var date = new DateOnly(2026, 11, 30);
        var req = RoomChangeRequest.Submit(
            Guid.NewGuid(), Guid.NewGuid(), room, Guid.NewGuid(), Guid.NewGuid(),
            date, "Documented new room request", Guid.NewGuid(), Now,
            ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var e = new UtilityMeter(Guid.NewGuid(), room, UtilityType.Electricity,
            "E-METER-PHASE2H-001", 0m,
            new DateTime(2026, 10, 1));
        var w = new UtilityMeter(Guid.NewGuid(), room, UtilityType.Water,
            "W-METER-PHASE2H-001", 0m,
            new DateTime(2026, 10, 1));
        var er = new MeterReading(Guid.NewGuid(), e.Id, 2026, 11,
            10m, 50m, new DateTime(2026, 11, 30));
        var wr = new MeterReading(Guid.NewGuid(), w.Id, 2026, 11,
            5m, 19m, new DateTime(2026, 11, 30));
        return (req, e, er, w, wr);
    }

    private static RoomChangeHandoverEvidenceRecord Attest(
        (RoomChangeRequest Request, UtilityMeter E, MeterReading Er,
            UtilityMeter W, MeterReading Wr) f)
        => RoomChangeHandoverEvidenceRecord.Attest(
            Guid.NewGuid(), f.Request, f.E, f.Er, f.W, f.Wr,
            "SIGNED-ROOM-INSPECTION-2026-001",
            "SIGNED-HANDOVER-EVIDENCE-2026-001",
            Guid.NewGuid(), Now.AddSeconds(1));

    [Fact]
    public void TRF39_Valid_Two_Physical_Meters_Produce_Immutable_Fingerprint()
    {
        var f = Fixture();
        var record = Attest(f);
        record.Verify(f.Request, f.E, f.Er, f.W, f.Wr);
        Assert.Equal(64, record.EvidenceSha256.Length);
        Assert.Equal(50m, record.ElectricityFinalReading);
        Assert.Equal(19m, record.WaterFinalReading);
        Assert.False(record.AreInvoiceAndDepositReconciled);
        Assert.False(record.IsTransferExecuted);
    }

    [Fact]
    public void TRF40_Wrong_Date_And_Wrong_Room_Are_Denied()
    {
        var f = Fixture();
        f.Er.Update(51m, new DateTime(2026, 11, 29), null);
        Assert.Throws<InvalidOperationException>(() => Attest(f));
        f = Fixture();
        var wrongWater = new UtilityMeter(Guid.NewGuid(), Guid.NewGuid(),
            UtilityType.Water, "WRONG-WATER-ROOM", 0m,
            new DateTime(2026, 10, 1));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeHandoverEvidenceRecord.Attest(Guid.NewGuid(), f.Request,
                f.E, f.Er, wrongWater, f.Wr,
                "SIGNED-ROOM-INSPECTION-2026-001",
                "SIGNED-HANDOVER-EVIDENCE-2026-001",
                Guid.NewGuid(), Now.AddSeconds(1)));
    }

    [Fact]
    public void TRF41_Missing_Signed_Inspection_Or_Handover_Is_Denied()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeHandoverEvidenceRecord.Attest(Guid.NewGuid(), f.Request,
                f.E, f.Er, f.W, f.Wr, "",
                "SIGNED-HANDOVER-EVIDENCE-2026-001",
                Guid.NewGuid(), Now.AddSeconds(1)));
    }

    [Fact]
    public void TRF42_PostAttestation_Meter_Reading_Drift_Is_Detected()
    {
        var f = Fixture();
        var record = Attest(f);
        f.Wr.Update(25m, new DateTime(2026, 11, 30), null);
        Assert.Throws<InvalidDataException>(() =>
            record.Verify(f.Request, f.E, f.Er, f.W, f.Wr));
    }

    [Fact]
    public void TRF43_Request_Scope_And_Utility_Type_Must_Match()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeHandoverEvidenceRecord.Attest(Guid.NewGuid(), f.Request,
                f.W, f.Wr, f.E, f.Er,
                "SIGNED-ROOM-INSPECTION-2026-001",
                "SIGNED-HANDOVER-EVIDENCE-2026-001",
                Guid.NewGuid(), Now.AddSeconds(1)));
    }
}
