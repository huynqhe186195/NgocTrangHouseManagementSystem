using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Utilities
{
    public class UtilityRateDto : AuditedEntityDto<Guid>
    {
        public Guid BuildingId { get; set; }

        public UtilityType UtilityType { get; set; }

        public decimal UnitPrice { get; set; }

        public DateTime EffectiveFrom { get; set; }

        public DateTime? EffectiveTo { get; set; }

        public string? Notes { get; set; }
    }
}
