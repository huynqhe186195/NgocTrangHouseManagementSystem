using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Contracts
{
    public class AddContractTenantDto
    {
        public Guid TenantId { get; set; }

        public ContractTenantRole Role { get; set; }
    }
}
