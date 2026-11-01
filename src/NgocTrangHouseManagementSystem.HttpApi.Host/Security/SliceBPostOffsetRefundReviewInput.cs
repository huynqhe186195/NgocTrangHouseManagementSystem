using System;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// An independent operator attests a MANUAL bank statement, not a gateway
/// callback. Monetary amount is read solely from the signed Owner decision.
/// </summary>
public sealed class SliceBPostOffsetRefundReviewInput
{
    public DateTime PaidAtUtc { get; set; }
    public string? TransactionReference { get; set; }
    public string? BankStatementEvidenceReference { get; set; }
    public string? SignedIndependentReviewReference { get; set; }
}
