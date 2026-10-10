using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace BuildingManagement.Contracts
{
    public class RenewContractDto
    {
        [Required]
        [MaxLength(64)]
        public string ContractNumber { get; set; } = default!;

        public DateTime StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public decimal MonthlyRent { get; set; }

        public decimal DepositAmount { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}
