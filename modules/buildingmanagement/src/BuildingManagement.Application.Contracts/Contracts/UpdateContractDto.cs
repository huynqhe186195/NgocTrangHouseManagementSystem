using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Contracts
{
    public class UpdateContractDto
    {
        [Required]
        [MaxLength(64)]
        public string ContractNumber { get; set; } = default!;

        public Guid RoomId { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public decimal MonthlyRent { get; set; }

        public decimal DepositAmount { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}