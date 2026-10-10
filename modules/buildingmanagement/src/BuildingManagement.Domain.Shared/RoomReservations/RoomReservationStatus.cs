using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.RoomReservations
{
    //Tiền thuê:             3.000.000
    //Tiền cọc yêu cầu:      3.000.000

    //B mới chuyển:            300.000
    //Còn thiếu:             2.700.000
    public enum RoomReservationStatus
    {
        PendingPayment = 1, //B chưa chuyển đồng nào
        Reserved = 2, //quyền giữ phòng có hiệu lực , B đã chuyển bất kỳ số tiền > 0
        ConvertedToContract = 3, // B đã ký hợp đồng thuê
        Cancelled = 4 //B hủy / không tới / reservation bị thay thế khi chưa trả tiền
    }
}
