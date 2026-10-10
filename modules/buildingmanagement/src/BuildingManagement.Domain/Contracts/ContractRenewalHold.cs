using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Contracts
{
    public class ContractRenewalHold
        : FullAuditedAggregateRoot<Guid>
    {
        public Guid CurrentContractId { get; private set; }

        public Guid RoomId { get; private set; }

        public DateTime RequestedAt { get; private set; }

        public DateTime ExpiresAt { get; private set; }

        public ContractRenewalHoldStatus Status { get; private set; }

        public Guid? CompletedContractId { get; private set; }

        public string? Notes { get; private set; }

        protected ContractRenewalHold()
        {
        }

        public ContractRenewalHold(
            Guid id,
            Guid currentContractId,
            Guid roomId,
            DateTime requestedAt,
            string? notes = null
        ) : base(id)
        {
            CurrentContractId = currentContractId;
            RoomId = roomId;

            RequestedAt = requestedAt;
            ExpiresAt = requestedAt.AddHours(24);

            Status = ContractRenewalHoldStatus.Active;

            CompletedContractId = null;
            Notes = notes;
        }

        public void Complete(Guid completedContractId)
        {
            CompletedContractId = completedContractId;
            Status = ContractRenewalHoldStatus.Completed;
        }

        public void Expire()
        {
            Status = ContractRenewalHoldStatus.Expired;
        }

        public void Cancel()
        {
            Status = ContractRenewalHoldStatus.Cancelled;
        }
    }
}