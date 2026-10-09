using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Utilities
{
    public class CreateUtilityMeterDto
    {
        public Guid RoomId { get; set; }

        public UtilityType UtilityType { get; set; }

        public string MeterCode { get; set; } = default!;

        public decimal InitialReading { get; set; }

        public DateTime InstalledDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}