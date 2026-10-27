using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only end-to-end issuance preflight. Reads the persisted bill,
/// immutable evidence and receipt anomalies; does NOT authorize, issue,
/// mutate Invoice, allocate Payment, settle deposit or end a contract.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalInvoiceIssuePreflightAppService : ApplicationService
{
    private readonly FinalMoveOutBillReviewAppService _billReview;
    private readonly FinalInvoiceEvidencePackageAppService _evidence;
    private readonly FinalMoveOutPaymentEvidenceAppService _paymentEvidence;

    public FinalInvoiceIssuePreflightAppService(
        FinalMoveOutBillReviewAppService billReview,
        FinalInvoiceEvidencePackageAppService evidence,
        FinalMoveOutPaymentEvidenceAppService paymentEvidence)
    {
        _billReview = billReview;
        _evidence = evidence;
        _paymentEvidence = paymentEvidence;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<FinalInvoiceIssuePreflight> GetAsync(Guid requestId)
    {
        var bill = await _billReview.GetAsync(requestId);
        // The payment review is read-only and includes historical receipt
        // anomalies even when a valid final utility draft is unavailable.
        var payments = await _paymentEvidence.GetAsync(requestId);
        FinalInvoiceEvidencePackage? package = null;
        if (bill.UtilityDraftId.HasValue &&
            bill.ProvisionalRentAndUtilities.HasValue)
        {
            try
            {
                package = await _evidence.GetAsync(requestId);
            }
            catch (BusinessException ex) when (
                ex.Code == InvoiceDraftErrorCodes.CandidateNotReady)
            {
                // The stored graph may be wrong-period or incomplete.
                // Record a blocker; do not manufacture a monetary total.
            }
        }

        return FinalInvoiceIssuePreflightEvaluator.Assess(
            bill, package, payments);
    }
}
