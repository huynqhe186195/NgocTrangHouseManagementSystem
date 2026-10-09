using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Contracts
{
    public class ContractTenantDto : AuditedEntityDto<Guid>
    {
        public Guid ContractId { get; set; }

        public Guid TenantId { get; set; }

        public ContractTenantRole Role { get; set; }
    }
}