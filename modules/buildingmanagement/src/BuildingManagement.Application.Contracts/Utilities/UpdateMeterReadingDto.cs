using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Utilities
{
    public class UpdateMeterReadingDto
    {
        public decimal CurrentReading { get; set; }

        public DateTime ReadingDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}