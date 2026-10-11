using System;
using System.Collections.Generic;
using System.Text;

namespace BuildingManagement.Payments
{
    public enum DepositDisposition
    {
        Unapplied = 1,
        AppliedToContract = 2,
        Refunded = 3,
        Forfeited = 4
    }
}
