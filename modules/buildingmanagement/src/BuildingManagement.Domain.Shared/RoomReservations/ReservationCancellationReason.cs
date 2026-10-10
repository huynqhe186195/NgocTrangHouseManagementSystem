using Castle.Components.DictionaryAdapter.Xml;
using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.RoomReservations
{
    //CustomerCancelled
    //→ B chủ động báo không thuê

    //NoShow
    //→ tới ngày nhận phòng nhưng B không tới

    //SupersededByRenewal
    //→ B chưa trả tiền
    //→ A được ưu tiên và ký renewal thành công
    //→ pending reservation của B bị kết thúc
    //public enum ReservationCancellationReason
    public enum ReservationCancellationReason
    {
        CustomerCancelled = 1,
        NoShow = 2,
        SupersededByRenewal = 3,
        Other = 4
    }
}
