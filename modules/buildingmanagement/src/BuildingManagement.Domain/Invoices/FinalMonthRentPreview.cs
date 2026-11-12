using System;
using System.Collections.Generic;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;

namespace BuildingManagement.Invoices;

/// <summary>
/// Read-only SRS rule: final month rent is the FULL contract monthly rent.
/// This is not a payable invoice, a receivable, or a deposit deduction.
/// Additional mid-month fees and proration are deliberately not inferred.
/// </summary>
public sealed record FinalMonthRentPreview(
    Guid MoveOutRequestId,
    int? BillingYear,
    int? BillingMonth,
    decimal? FullMonthRent,
    IReadOnlyList<string> Blockers)
{
    public bool CanIssue => false;

    public static FinalMonthRentPreview Assess(
        MoveOutRequest request, Contract? contract)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blockers = new List<string>();
        if (request.Status != MoveOutRequestStatus.Approved ||
            request.ApprovedMoveOutDate is null)
            blockers.Add("MoveOutNotApproved");

        var date = request.ApprovedMoveOutDate;
        if (contract is null ||
            contract.Id != request.ContractId ||
            contract.RoomId != request.RoomId ||
            contract.Status != ContractStatus.Active ||
            contract.MonthlyRent <= 0 ||
            (date.HasValue &&
             contract.StartDate.Date > date.Value.ToDateTime(TimeOnly.MinValue).Date))
            blockers.Add("ContractNotEligible");

        decimal? rent = blockers.Count == 0 ? contract!.MonthlyRent : null;
        // The final bill still requires meter review, issue, debt settlement,
        // and deposit approval. A quote cannot authorize any write.
        blockers.Add("FinalInvoiceNotIssued");
        return new FinalMonthRentPreview(
            request.Id, date?.Year, date?.Month, rent,
            blockers.AsReadOnly());
    }
}
