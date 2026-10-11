using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Payments
{
        //    ReservationDeposit
        //→ tiền B cọc giữ phòng

        //ContractDeposit
        //→ tiền cọc theo hợp đồng

        //InvoicePayment
        //→ tiền thanh toán hóa đơn
    public enum PaymentPurpose
    {
        ReservationDeposit = 1,
        ContractDeposit = 2,
        InvoicePayment = 3
    }
}
