using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Utilities
{
    public class UpdateUtilityMeterDto
    {
        public string MeterCode { get; set; } = default!;

        public decimal InitialReading { get; set; }

        public DateTime InstalledDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}