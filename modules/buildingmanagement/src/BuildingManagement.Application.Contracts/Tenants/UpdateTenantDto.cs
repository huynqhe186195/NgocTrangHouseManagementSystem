using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Tenants
{
    public class UpdateTenantDto
    {
        [Required]
        [MaxLength(128)]
        public string FullName { get; set; } = default!;

        [MaxLength(20)]
        public string? PhoneNumber { get; set; }

        [EmailAddress]
        [MaxLength(256)]
        public string? Email { get; set; }

        public DateTime? DateOfBirth { get; set; }

        public Gender? Gender { get; set; }

        public IdentityType? IdentityType { get; set; }

        [MaxLength(50)]
        public string? IdentityNumber { get; set; }

        [MaxLength(256)]
        public string? PermanentAddress { get; set; }

        [MaxLength(128)]
        public string? EmergencyContactName { get; set; }

        [MaxLength(20)]
        public string? EmergencyContactPhone { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}
