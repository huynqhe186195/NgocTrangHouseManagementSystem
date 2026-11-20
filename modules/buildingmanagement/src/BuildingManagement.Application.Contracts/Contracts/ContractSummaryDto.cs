using System;
using System.Collections.Generic;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Contracts
{
    /// <summary>
    /// Contract plus the names a list screen has to print. <see cref="ContractDto"/>
    /// deliberately carries only ids, because the contract entity has no building
    /// of its own: the building is reached through Room -> Floor. A screen cannot
    /// resolve that without loading three other tables, so this DTO carries the
    /// names it would otherwise have to guess at.
    /// </summary>
    public class ContractSummaryDto : AuditedEntityDto<Guid>
    {
        public string ContractNumber { get; set; } = default!;

        public ContractStatus Status { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public DateTime? ActualEndedAt { get; set; }

        public ContractEndReason? EndReason { get; set; }

        public decimal MonthlyRent { get; set; }

        public decimal DepositAmount { get; set; }

        public string? Notes { get; set; }

        public Guid RoomId { get; set; }

        public string RoomNumber { get; set; } = default!;

        public Guid FloorId { get; set; }

        public string FloorName { get; set; } = default!;

        public Guid BuildingId { get; set; }

        public string BuildingName { get; set; } = default!;

        public List<ContractTenantSummaryDto> Tenants { get; set; } = new();
    }

    public class ContractTenantSummaryDto
    {
        public Guid TenantId { get; set; }

        public string FullName { get; set; } = default!;

        public ContractTenantRole Role { get; set; }
    }
}
