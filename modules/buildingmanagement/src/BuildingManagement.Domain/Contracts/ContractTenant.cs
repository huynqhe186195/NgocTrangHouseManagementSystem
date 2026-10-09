using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Contracts
{
    public class ContractTenant : FullAuditedEntity<Guid>
    {
        public Guid ContractId { get; private set; }

        public Guid TenantId { get; private set; }

        public ContractTenantRole Role { get; private set; }

        protected ContractTenant()
        {
        }

        public ContractTenant(
            Guid id,
            Guid contractId,
            Guid tenantId,
            ContractTenantRole role
        ) : base(id)
        {
            ContractId = contractId;
            TenantId = tenantId;
            Role = role;
        }

        public void UpdateRole(
            ContractTenantRole role)
        {
            Role = role;
        }
    }
}