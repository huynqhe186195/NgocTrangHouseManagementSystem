using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Rooms
{
    public class RoomDto : AuditedEntityDto<Guid>
    {
        public Guid FloorId { get; set; }

        public string RoomNumber { get; set; } = default!;

        public string Name { get; set; } = default!;

        public decimal? Area { get; set; }

        public int Capacity { get; set; }

        public RoomStatus Status { get; set; }

        public string? Description { get; set; }
    }
}
