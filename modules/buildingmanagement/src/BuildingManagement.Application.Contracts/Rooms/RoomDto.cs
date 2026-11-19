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

        public string? RoomType { get; set; }

        /// <summary>Free text, e.g. "Gần cầu thang". Null when unset.</summary>
        public string? LocationNote { get; set; }

        public RoomDirection? Direction { get; set; }

        public decimal? MonthlyRent { get; set; }

        public decimal? LayoutX { get; set; }

        public decimal? LayoutY { get; set; }

        public decimal? LayoutWidth { get; set; }

        public decimal? LayoutHeight { get; set; }

        public RoomLayoutShape? LayoutShape { get; set; }

        public decimal? LayoutRotationDegrees { get; set; }

        public string? LayoutPoints { get; set; }

        public int EquipmentCount { get; set; }

        public int PhotoCount { get; set; }
    }
}
