using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Contracts
{
    public class Contract : FullAuditedAggregateRoot<Guid>
    {
        public string ContractNumber { get; private set; } = default!;

        public Guid RoomId { get; private set; }

        public DateTime StartDate { get; private set; }

        public DateTime? EndDate { get; private set; }

        public decimal MonthlyRent { get; private set; }

        public decimal DepositAmount { get; private set; }

        public ContractStatus Status { get; private set; }

        public string? Notes { get; private set; }

        protected Contract()
        {
        }

        public Contract(
            Guid id,
            string contractNumber,
            Guid roomId,
            DateTime startDate,
            DateTime? endDate,
            decimal monthlyRent,
            decimal depositAmount,
            string? notes = null
        ) : base(id)
        {
            ContractNumber = contractNumber;
            RoomId = roomId;
            StartDate = startDate;
            EndDate = endDate;
            MonthlyRent = monthlyRent;
            DepositAmount = depositAmount;
            Status = ContractStatus.Draft;
            Notes = notes;
        }

        public void Update(
            string contractNumber,
            Guid roomId,
            DateTime startDate,
            DateTime? endDate,
            decimal monthlyRent,
            decimal depositAmount,
            string? notes)
        {
            ContractNumber = contractNumber;
            RoomId = roomId;
            StartDate = startDate;
            EndDate = endDate;
            MonthlyRent = monthlyRent;
            DepositAmount = depositAmount;
            Notes = notes;
        }

        public void Activate()
        {
            Status = ContractStatus.Active;
        }

        public void End()
        {
            Status = ContractStatus.Ended;
        }

        public void Cancel()
        {
            Status = ContractStatus.Cancelled;
        }
    }
}