using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Utilities;

public class MeterReplacementDto : AuditedEntityDto<Guid>
{
    public Guid OldMeterId { get; set; }
    public Guid NewMeterId { get; set; }
    public Guid RoomId { get; set; }
    public UtilityType UtilityType { get; set; }
    public DateTime ReplacementDate { get; set; }
    public decimal OldPreviousReading { get; set; }
    public decimal OldFinalReading { get; set; }
    public decimal NewInitialReading { get; set; }
    public decimal ClosingUsage => OldFinalReading - OldPreviousReading;
    public string Reason { get; set; } = default!;
    public string? Notes { get; set; }
}
