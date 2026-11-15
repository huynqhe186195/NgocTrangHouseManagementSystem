using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Rooms
{
    public class RoomEquipmentDto
    {
        public Guid Id { get; set; }

        public Guid RoomId { get; set; }

        public string Name { get; set; } = default!;

        public string? Category { get; set; }

        public int Quantity { get; set; }

        public RoomEquipmentCondition Condition { get; set; }

        public DateTime? InstalledDate { get; set; }

        public string? Notes { get; set; }
    }

    public class CreateRoomEquipmentDto
    {
        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = default!;

        [MaxLength(64)]
        public string? Category { get; set; }

        [Range(0, 1000)]
        public int Quantity { get; set; } = 1;

        public RoomEquipmentCondition Condition { get; set; }
            = RoomEquipmentCondition.Good;

        public DateTime? InstalledDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }

    public class UpdateRoomEquipmentDto
    {
        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = default!;

        [MaxLength(64)]
        public string? Category { get; set; }

        [Range(0, 1000)]
        public int Quantity { get; set; } = 1;

        public RoomEquipmentCondition Condition { get; set; }
            = RoomEquipmentCondition.Good;

        public DateTime? InstalledDate { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}
