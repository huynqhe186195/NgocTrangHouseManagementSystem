using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Utilities
{
    public class MeterReading
        : FullAuditedAggregateRoot<Guid>
    {
        public Guid UtilityMeterId { get; private set; }

        public int BillingYear { get; private set; }

        public int BillingMonth { get; private set; }

        public decimal PreviousReading { get; private set; }

        public decimal CurrentReading { get; private set; }

        public DateTime ReadingDate { get; private set; }

        public string? Notes { get; private set; }

        protected MeterReading()
        {
        }

        public MeterReading(
            Guid id,
            Guid utilityMeterId,
            int billingYear,
            int billingMonth,
            decimal previousReading,
            decimal currentReading,
            DateTime readingDate,
            string? notes = null
        ) : base(id)
        {
            UtilityMeterId = utilityMeterId;
            BillingYear = billingYear;
            BillingMonth = billingMonth;
            PreviousReading = previousReading;
            CurrentReading = currentReading;
            ReadingDate = readingDate;
            Notes = notes;
        }

        public void Update(
            decimal currentReading,
            DateTime readingDate,
            string? notes)
        {
            CurrentReading = currentReading;
            ReadingDate = readingDate;
            Notes = notes;
        }
    }
}