using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Floors
{
    public class FloorDto : AuditedEntityDto<Guid>
    {
        public Guid BuildingId { get; set; }

        public int FloorNumber { get; set; }

        public string Name { get; set; } = default!;

        public string? Description { get; set; }
    }
}
