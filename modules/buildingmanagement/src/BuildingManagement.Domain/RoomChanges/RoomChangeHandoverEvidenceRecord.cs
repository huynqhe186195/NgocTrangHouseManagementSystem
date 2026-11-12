using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Immutable OPERATOR-ATTESTED physical handover, anchored to two server
/// materialized original physical meter readings on the requested date.
/// Document references are not a digital-signature verification and DO NOT
/// settle invoices, close a contract or move a deposit.
/// </summary>
public sealed class RoomChangeHandoverEvidenceRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public DateOnly HandoverDate { get; private set; }
    public Guid ElectricityMeterId { get; private set; }
    public Guid ElectricityReadingId { get; private set; }
    public decimal ElectricityFinalReading { get; private set; }
    public Guid WaterMeterId { get; private set; }
    public Guid WaterReadingId { get; private set; }
    public decimal WaterFinalReading { get; private set; }
    public string InspectionEvidenceReference { get; private set; } = string.Empty;
    public string SignedHandoverReference { get; private set; } = string.Empty;
    public Guid AttestedByUserId { get; private set; }
    public DateTime AttestedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeHandoverEvidenceRecord() { }
    private RoomChangeHandoverEvidenceRecord(Guid id) : base(id) { }

    public static RoomChangeHandoverEvidenceRecord Attest(
        Guid id, RoomChangeRequest request,
        UtilityMeter electricityMeter, MeterReading electricityReading,
        UtilityMeter waterMeter, MeterReading waterReading,
        string? inspectionReference, string? handoverReference,
        Guid actor, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(electricityMeter);
        ArgumentNullException.ThrowIfNull(electricityReading);
        ArgumentNullException.ThrowIfNull(waterMeter);
        ArgumentNullException.ThrowIfNull(waterReading);
        var inspection = Evidence(inspectionReference);
        var signed = Evidence(handoverReference);
        if (id == Guid.Empty || actor == Guid.Empty ||
            atUtc.Kind != DateTimeKind.Utc ||
            atUtc < request.CreatedAtUtc ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            request.Id == Guid.Empty || request.SourceContractId == Guid.Empty ||
            request.BuildingId == Guid.Empty || request.SourceRoomId == Guid.Empty ||
            request.RequestedDate == default ||
            !Valid(electricityMeter, electricityReading,
                UtilityType.Electricity, request) ||
            !Valid(waterMeter, waterReading,
                UtilityType.Water, request) ||
            electricityMeter.Id == waterMeter.Id ||
            electricityReading.Id == waterReading.Id)
            throw new InvalidOperationException(
                "Handover requires live, distinct physical E/W reading evidence for the source room on its transfer date.");

        var result = new RoomChangeHandoverEvidenceRecord(id)
        {
            RequestId = request.Id,
            SourceContractId = request.SourceContractId,
            SourceRoomId = request.SourceRoomId,
            BuildingId = request.BuildingId,
            HandoverDate = request.RequestedDate,
            ElectricityMeterId = electricityMeter.Id,
            ElectricityReadingId = electricityReading.Id,
            ElectricityFinalReading = electricityReading.CurrentReading,
            WaterMeterId = waterMeter.Id,
            WaterReadingId = waterReading.Id,
            WaterFinalReading = waterReading.CurrentReading,
            InspectionEvidenceReference = inspection,
            SignedHandoverReference = signed,
            AttestedByUserId = actor,
            AttestedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10L))
        };
        result.EvidenceSha256 = result.Hash();
        return result;
    }

    public void Verify(RoomChangeRequest request,
        UtilityMeter electricityMeter, MeterReading electricityReading,
        UtilityMeter waterMeter, MeterReading waterReading)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RequestId != request.Id ||
            SourceContractId != request.SourceContractId ||
            SourceRoomId != request.SourceRoomId ||
            BuildingId != request.BuildingId ||
            HandoverDate != request.RequestedDate ||
            AttestedByUserId == Guid.Empty || AttestedAtUtc.Kind != DateTimeKind.Utc ||
            AttestedAtUtc < request.CreatedAtUtc ||
            ElectricityMeterId != electricityMeter.Id ||
            ElectricityReadingId != electricityReading.Id ||
            WaterMeterId != waterMeter.Id ||
            WaterReadingId != waterReading.Id ||
            ElectricityFinalReading != electricityReading.CurrentReading ||
            WaterFinalReading != waterReading.CurrentReading ||
            !Valid(electricityMeter, electricityReading, UtilityType.Electricity, request) ||
            !Valid(waterMeter, waterReading, UtilityType.Water, request) ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("Original meter or signed handover evidence has drifted.");
    }

    private static bool Valid(UtilityMeter meter, MeterReading reading,
        UtilityType type, RoomChangeRequest request)
        => meter.Id != Guid.Empty &&
           !meter.IsDeleted && !reading.IsDeleted &&
           meter.RoomId == request.SourceRoomId &&
           meter.UtilityType == type &&
           meter.InstalledDate.Date <= request.RequestedDate.ToDateTime(TimeOnly.MinValue) &&
           reading.Id != Guid.Empty && reading.UtilityMeterId == meter.Id &&
           reading.BillingYear == request.RequestedDate.Year &&
           reading.BillingMonth == request.RequestedDate.Month &&
           DateOnly.FromDateTime(reading.ReadingDate) == request.RequestedDate &&
           reading.PreviousReading >= 0m &&
           reading.CurrentReading >= reading.PreviousReading;

    private static string Evidence(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is < 12 or > 500)
            throw new InvalidOperationException(
                "Inspection and signed handover require identifiable external evidence references.");
        return text;
    }

    private string Hash()
    {
        var canonical = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), SourceRoomId.ToString("D"),
            BuildingId.ToString("D"), HandoverDate.ToString("yyyy-MM-dd"),
            ElectricityMeterId.ToString("D"), ElectricityReadingId.ToString("D"),
            ElectricityFinalReading.ToString("0.000", CultureInfo.InvariantCulture),
            WaterMeterId.ToString("D"), WaterReadingId.ToString("D"),
            WaterFinalReading.ToString("0.000", CultureInfo.InvariantCulture),
            InspectionEvidenceReference, SignedHandoverReference,
            AttestedByUserId.ToString("D"), AttestedAtUtc.ToString("O")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public bool IsTransferExecuted => false;
    public bool AreInvoiceAndDepositReconciled => false;
}
