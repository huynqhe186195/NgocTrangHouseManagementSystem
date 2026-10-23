using System;
using System.Collections.Generic;
using BuildingManagement.MoveOuts;

namespace BuildingManagement.Utilities;

/// <summary>
/// Read-only evidence preflight tied to an approved move-out request.
/// "HasReadingEvidenceForBothUtilities" is NOT sign-off of the checklist.
/// </summary>
public sealed class MoveOutFinalMeterReadinessDto
{
    public Guid MoveOutRequestId { get; set; }
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public MoveOutRequestStatus RequestStatus { get; set; }
    public DateOnly? ApprovedMoveOutDate { get; set; }
    public List<MoveOutFinalMeterEvidenceDto> Utilities { get; set; } = new();
    public bool HasReadingEvidenceForBothUtilities =>
        RequestStatus == MoveOutRequestStatus.Approved &&
        Utilities.Count == 2 &&
        Utilities.TrueForAll(x => x.Status == "ReadingEvidencePresent");

    // The final checklist and invoice approval workflows have not been
    // implemented; do not present this read-only assessment as approval.
    public bool IsFinalBillApproved => false;
}

public sealed class MoveOutFinalMeterEvidenceDto
{
    public UtilityType UtilityType { get; set; }
    public Guid? UtilityMeterId { get; set; }
    public Guid? MeterReadingId { get; set; }
    public decimal? FinalReading { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> CoverageIssues { get; set; } = new();
}
