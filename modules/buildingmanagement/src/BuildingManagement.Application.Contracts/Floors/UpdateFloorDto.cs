using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Floors
{
    public class UpdateFloorDto
    {
        public int FloorNumber { get; set; }

        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = default!;

        [MaxLength(500)]
        public string? Description { get; set; }
    }
}
