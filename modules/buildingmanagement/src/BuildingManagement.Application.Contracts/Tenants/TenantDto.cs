using Abp.Application.Services.Dto;
using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Tenants
{
    public class TenantDto : AuditedEntityDto<Guid>
    {
        public string FullName { get; set; } = default!;

        public string? PhoneNumber { get; set; }

        public string? Email { get; set; }

        public DateTime? DateOfBirth { get; set; }

        public Gender? Gender { get; set; }

        public IdentityType? IdentityType { get; set; }

        public string? IdentityNumber { get; set; }

        public string? PermanentAddress { get; set; }

        public string? EmergencyContactName { get; set; }

        public string? EmergencyContactPhone { get; set; }

        public string? Notes { get; set; }
    }
}
