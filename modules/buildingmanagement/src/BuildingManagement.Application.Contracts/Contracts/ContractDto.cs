using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Contracts
{
    public class ContractDto : AuditedEntityDto<Guid>
    {
        public string ContractNumber { get; set; } = default!;

        public Guid RoomId { get; set; }

        public Guid? RenewedFromContractId { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public DateTime? ActualEndedAt { get; set; }

        public ContractEndReason? EndReason { get; set; }

        public decimal MonthlyRent { get; set; }

        public decimal DepositAmount { get; set; }

        public ContractStatus Status { get; set; }

        public string? Notes { get; set; }
    }
}