using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Utilities
{
    public class UpdateUtilityRateDto
    {
        public Guid BuildingId { get; set; }

        public UtilityType UtilityType { get; set; }

        public decimal UnitPrice { get; set; }

        public DateTime EffectiveFrom { get; set; }

        public DateTime? EffectiveTo { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}
