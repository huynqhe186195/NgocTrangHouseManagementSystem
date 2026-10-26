using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only review of payment records that appear to be associated
/// with the final month. It includes historical soft-deleted financial
/// records for anomaly detection, never authorizes payment posting,
/// deposit payout, invoice issuance or debt write-off.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalMoveOutPaymentEvidenceAppService : ApplicationService
{
    private readonly FinalMoveOutBillReviewAppService _finalBill;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public FinalMoveOutPaymentEvidenceAppService(
        FinalMoveOutBillReviewAppService finalBill,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _finalBill = finalBill;
        _payments = payments;
        _softDeleteFilter = softDeleteFilter;
    }

    public virtual async Task<FinalMoveOutPaymentEvidence> GetAsync(
        Guid moveOutRequestId)
    {
        var preview = await _finalBill.GetAsync(moveOutRequestId);

        // The ID/contract come exclusively from server-verified review.
        // Including a non-matching contract linked to this draft flags
        // suspicious cross-contract attribution rather than silently hiding it.
        List<Payment> records;
        using (_softDeleteFilter.Disable())
        {
            records = await _payments.GetListAsync(payment =>
                payment.ContractId == preview.ContractId ||
                (preview.UtilityDraftId.HasValue &&
                 payment.InvoiceId == preview.UtilityDraftId.Value));
        }

        return FinalMoveOutPaymentEvidenceEvaluator.Assess(preview, records);
    }
}
