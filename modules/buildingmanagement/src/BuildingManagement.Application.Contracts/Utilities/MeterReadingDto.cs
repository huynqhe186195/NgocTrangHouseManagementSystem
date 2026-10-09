using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Utilities
{
    public class MeterReadingDto : AuditedEntityDto<Guid>
    {
        public Guid UtilityMeterId { get; set; }

        public int BillingYear { get; set; }

        public int BillingMonth { get; set; }

        public decimal PreviousReading { get; set; }

        public decimal CurrentReading { get; set; }

        public DateTime ReadingDate { get; set; }

        public string? Notes { get; set; }

        public decimal Usage =>
            CurrentReading - PreviousReading;
    }
}