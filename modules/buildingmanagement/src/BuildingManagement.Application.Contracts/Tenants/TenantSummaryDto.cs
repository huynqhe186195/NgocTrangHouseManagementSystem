using System;
using System.Collections.Generic;
using Abp.Application.Services.Dto;

namespace BuildingManagement.Tenants
{
    /// <summary>
    /// A tenant as the admin list shows them, with the contracts that make
    /// them visible to this caller.
    ///
    /// <see cref="TenantDto"/> is the write model and carries every PII field.
    /// This read model sends only what the roster needs, plus where the person
    /// currently rents, because a tenant with no contract in an authorized
    /// building is not in this list at all.
    /// </summary>
    public class TenantSummaryDto : AuditedEntityDto<Guid>
    {
        public string FullName { get; set; } = default!;
        public string PhoneNumber { get; set; } = default!;
        public string? Email { get; set; }
        public Gender? Gender { get; set; }
        public string? PermanentAddress { get; set; }
        public string? Notes { get; set; }

        /// <summary>
        /// The dwellings this person holds a live contract on, restricted to
        /// the buildings the caller may see. A tenant can appear in more than
        /// one building, which is why this is a list.
        /// </summary>
        public List<TenantContractSummaryDto> Contracts { get; set; } = new();
    }

    public class TenantContractSummaryDto
    {
        public Guid ContractId { get; set; }
        public string ContractNumber { get; set; } = default!;
        public int Status { get; set; }
        /// <summary>
        /// Sent because the roster has to answer "who is leaving soon" without
        /// a second call per person.
        /// </summary>
        public DateTime? EndDate { get; set; }
        public string RoomNumber { get; set; } = default!;
        public string FloorName { get; set; } = default!;
        public string BuildingName { get; set; } = default!;
        public Guid RoomId { get; set; }
        public Guid BuildingId { get; set; }
    }
}
