using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Buildings
{
    public class BuildingDto : AuditedEntityDto<Guid>
    {
        public string Name { get; set; } = default!;

        public string Address { get; set; } = default!;

        public string? Description { get; set; }
    }
}
