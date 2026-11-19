using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Rooms
{
    public class UpdateRoomDto
    {
        [Required]
        [MaxLength(32)]
        public string RoomNumber { get; set; } = default!;

        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = default!;

        [Range(0.1, 10000)]
        public decimal? Area { get; set; }

        [Range(1, 100)]
        public int Capacity { get; set; }

        public RoomStatus Status { get; set; }

        [MaxLength(500)]
        public string? Description { get; set; }

        [MaxLength(64)]
        public string? RoomType { get; set; }

        [MaxLength(128)]
        public string? LocationNote { get; set; }

        public RoomDirection? Direction { get; set; }
    }
}
