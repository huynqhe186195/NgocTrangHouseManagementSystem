using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Payments
{
    public class CompletePaymentDto
    {
        [MaxLength(128)]
        public string? TransactionReference
        {
            get;
            set;
        }
    }
}