using Volo.Abp.Domain.Entities.Auditing;
using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Utilities
{
    public class UtilityRate
        : FullAuditedAggregateRoot<Guid>
    {
        public Guid BuildingId { get; private set; }

        public UtilityType UtilityType { get; private set; }

        public decimal UnitPrice { get; private set; }

        public DateTime EffectiveFrom { get; private set; }

        public DateTime? EffectiveTo { get; private set; }

        public string? Notes { get; private set; }

        protected UtilityRate()
        {
        }

        public UtilityRate(
            Guid id,
            Guid buildingId,
            UtilityType utilityType,
            decimal unitPrice,
            DateTime effectiveFrom,
            DateTime? effectiveTo,
            string? notes = null
        ) : base(id)
        {
            BuildingId = buildingId;
            UtilityType = utilityType;
            UnitPrice = unitPrice;
            EffectiveFrom = effectiveFrom;
            EffectiveTo = effectiveTo;
            Notes = notes;
        }

        public void Update(
            Guid buildingId,
            UtilityType utilityType,
            decimal unitPrice,
            DateTime effectiveFrom,
            DateTime? effectiveTo,
            string? notes)
        {
            BuildingId = buildingId;
            UtilityType = utilityType;
            UnitPrice = unitPrice;
            EffectiveFrom = effectiveFrom;
            EffectiveTo = effectiveTo;
            Notes = notes;
        }
    }
}
