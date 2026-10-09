using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Utilities
{
    public class UtilityMeterDto : AuditedEntityDto<Guid>
    {
        public Guid RoomId { get; set; }

        public UtilityType UtilityType { get; set; }

        public string MeterCode { get; set; } = default!;

        public decimal InitialReading { get; set; }

        public DateTime InstalledDate { get; set; }

        public bool IsActive { get; set; }

        public string? Notes { get; set; }
    }
}