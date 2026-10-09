using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Utilities
{
    public class CreateMeterReadingDto
    {
        public Guid UtilityMeterId { get; set; }

        public int BillingYear { get; set; }

        public int BillingMonth { get; set; }

        public decimal CurrentReading { get; set; }

        public DateTime ReadingDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}