using System;
using BuildingManagement.DepositSettlements;

namespace BuildingManagement.Payments;

public class ReservationDepositSettlementDto
{
    public Guid RoomReservationId { get; set; }

    public DepositSettlementType DispositionType
    {
        get;
        set;
    }

    public decimal TotalPaid { get; set; }

    // Số tiền xử lý thêm trong lần gọi này
    public decimal SettledNow { get; set; }

    // Tổng đã refund/forfeit sau operation
    public decimal TotalSettled { get; set; }
}