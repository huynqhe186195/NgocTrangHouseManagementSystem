using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Payments
{
    public enum PaymentStatus
    {
        Pending = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4
    }
}
