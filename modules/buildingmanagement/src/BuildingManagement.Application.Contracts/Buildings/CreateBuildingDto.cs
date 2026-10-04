using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Buildings
{
    public class CreateBuildingDto
    {
        [Required]
        [MaxLength(128)]
        public string Name { get; set; } = default!;


        [Required]
        [MaxLength(256)]
        public string Address { get; set; } = default!;


        [MaxLength(500)]
        public string? Description { get; set; }
    }
}
