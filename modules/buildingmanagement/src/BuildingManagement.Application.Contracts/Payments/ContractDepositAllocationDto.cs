using System;

namespace BuildingManagement.Payments;

public class ContractDepositAllocationDto
{
    public Guid RoomReservationId { get; set; }

    public Guid ContractId { get; set; }

    // Số tiền được allocate thêm trong lần gọi này
    public decimal AllocatedAmount { get; set; }

    // Tổng cọc Contract đã được cover sau operation
    public decimal CoveredDeposit { get; set; }

    public decimal RemainingDeposit { get; set; }
}