using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Utilities;

/// <summary>
/// Replace one ACTIVE meter. RoomId/UtilityType always inherit from the old meter.
/// </summary>
public class ReplaceUtilityMeterDto
{
    [Required]
    [MaxLength(64)]
    public string NewMeterCode { get; set; } = default!;

    public decimal OldFinalReading { get; set; }
    public decimal NewInitialReading { get; set; }
    public DateTime ReplacementDate { get; set; }

    [Required]
    [MaxLength(500)]
    public string Reason { get; set; } = default!;

    [MaxLength(500)]
    public string? Notes { get; set; }
}
