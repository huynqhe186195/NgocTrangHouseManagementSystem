using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Contracts
{
    public class ContractRenewalHoldDto
        : AuditedEntityDto<Guid>
    {
        public Guid CurrentContractId { get; set; }

        public Guid RoomId { get; set; }

        public DateTime RequestedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public ContractRenewalHoldStatus Status { get; set; }

        public Guid? CompletedContractId { get; set; }

        public string? Notes { get; set; }
    }
}