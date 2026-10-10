using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Contracts
{
    public class CreateContractRenewalHoldDto
    {
        public Guid CurrentContractId { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}