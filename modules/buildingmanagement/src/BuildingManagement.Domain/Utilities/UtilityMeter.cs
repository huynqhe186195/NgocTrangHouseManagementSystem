using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Utilities
{
    public class UtilityMeter
        : FullAuditedAggregateRoot<Guid>
    {
        public Guid RoomId { get; private set; }

        public UtilityType UtilityType { get; private set; }

        public string MeterCode { get; private set; } = default!;

        public decimal InitialReading { get; private set; }

        public DateTime InstalledDate { get; private set; }

        public bool IsActive { get; private set; }

        public string? Notes { get; private set; }

        protected UtilityMeter()
        {
        }

        public UtilityMeter(
            Guid id,
            Guid roomId,
            UtilityType utilityType,
            string meterCode,
            decimal initialReading,
            DateTime installedDate,
            string? notes = null
        ) : base(id)
        {
            RoomId = roomId;
            UtilityType = utilityType;
            MeterCode = meterCode;
            InitialReading = initialReading;
            InstalledDate = installedDate;
            IsActive = true;
            Notes = notes;
        }

        public void Update(
            string meterCode,
            decimal initialReading,
            DateTime installedDate,
            string? notes)
        {
            MeterCode = meterCode;
            InitialReading = initialReading;
            InstalledDate = installedDate;
            Notes = notes;
        }

        public void Deactivate()
        {
            IsActive = false;
        }
    }
}