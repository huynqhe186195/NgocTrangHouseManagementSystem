using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// INTERNAL, REPEATABLE-READ composition of real official MONTHLY invoices
/// with the existing independently audited held-deposit ledger. Never
/// trusts HTTP money/verification flags. All contract payments, corrections,
/// journals, and competing disposition decisions are examined together.
/// This is not a settlement approval and deliberately exposes NO remote API.
/// </summary>
[RemoteService(IsEnabled = false)]
public class ContractDepositOfficialOffsetReadAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<MonthlyIssuedInvoiceRecord, Guid> _monthly;
    private readonly IRepository<IssuedInvoiceLedgerRecord, Guid> _final;
    private readonly IRepository<OfficialInvoiceReceivablePostingRecord, Guid> _postings;
    private readonly IRepository<MonthlyInvoiceCandidateLedgerRecord, Guid> _candidates;
    private readonly IRepository<MonthlyInvoiceIndependentReviewRecord, Guid> _reviews;
    private readonly IRepository<MonthlyInvoicePaymentAllocationRecord, Guid> _allocations;
    private readonly IRepository<OfficialMonthlyReceiptLineAllocationRecord, Guid> _lineAllocations;
    private readonly IRepository<InvoicePaymentAllocationJournalEntry, Guid> _finalAllocations;
    private readonly IRepository<OfficialInvoiceReceiptReviewRecord, Guid> _receiptReviews;
    private readonly IRepository<MonthlyInvoiceAdjustmentRecord, Guid> _adjustments;
    private readonly IRepository<MonthlyInvoiceAdjustmentDecisionRecord, Guid> _adjustmentDecisions;
    private readonly IRepository<ContractDepositSettlementDecisionRecord, Guid> _decisions;
    private readonly IRepository<ContractDepositCloseoutApprovalRecord, Guid> _closeoutApprovals;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDelete;
    private readonly ContractDepositLedgerReviewAppService _depositReader;
    private readonly IInvoiceDraftReadIsolationGuard _isolation;

    public ContractDepositOfficialOffsetReadAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts,
        IRepository<MonthlyIssuedInvoiceRecord, Guid> monthly,
        IRepository<IssuedInvoiceLedgerRecord, Guid> final,
        IRepository<OfficialInvoiceReceivablePostingRecord, Guid> postings,
        IRepository<MonthlyInvoiceCandidateLedgerRecord, Guid> candidates,
        IRepository<MonthlyInvoiceIndependentReviewRecord, Guid> reviews,
        IRepository<MonthlyInvoicePaymentAllocationRecord, Guid> allocations,
        IRepository<OfficialMonthlyReceiptLineAllocationRecord, Guid> lineAllocations,
        IRepository<InvoicePaymentAllocationJournalEntry, Guid> finalAllocations,
        IRepository<OfficialInvoiceReceiptReviewRecord, Guid> receiptReviews,
        IRepository<MonthlyInvoiceAdjustmentRecord, Guid> adjustments,
        IRepository<MonthlyInvoiceAdjustmentDecisionRecord, Guid> adjustmentDecisions,
        IRepository<ContractDepositSettlementDecisionRecord, Guid> decisions,
        IRepository<ContractDepositCloseoutApprovalRecord, Guid> closeoutApprovals,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDelete,
        ContractDepositLedgerReviewAppService depositReader,
        IInvoiceDraftReadIsolationGuard isolation)
    {
        _requests = requests;
        _contracts = contracts;
        _monthly = monthly;
        _final = final;
        _postings = postings;
        _candidates = candidates;
        _reviews = reviews;
        _allocations = allocations;
        _lineAllocations = lineAllocations;
        _finalAllocations = finalAllocations;
        _receiptReviews = receiptReviews;
        _adjustments = adjustments;
        _adjustmentDecisions = adjustmentDecisions;
        _decisions = decisions;
        _closeoutApprovals = closeoutApprovals;
        _payments = payments;
        _softDelete = softDelete;
        _depositReader = depositReader;
        _isolation = isolation;
    }

    [UnitOfWork(true, IsolationLevel.RepeatableRead)]
    public virtual async Task<ContractDepositOfficialOffsetRead> GetAsync(
        Guid requestId, DateOnly asOfDate)
    {
        if (requestId == Guid.Empty || asOfDate == default)
            throw new ArgumentException("Move-out request and Vietnam as-of date required.");
        await _isolation.RequireRepeatableReadAsync();

        var request = await _requests.GetAsync(requestId);
        var contract = await _contracts.GetAsync(request.ContractId);
        var deposit = await _depositReader.GetAsync(requestId);
        var blockers = new List<string>();

        // The existing deposit audit purposely emits three policy blockers:
        // they are NOT proof of settlement authority. Every other anomaly
        // means the actual held amount cannot be trusted for a preview.
        if (deposit.ContractId != contract.Id ||
            deposit.MoveOutRequestId != request.Id ||
            request.RoomId != contract.RoomId ||
            request.Status != MoveOutRequestStatus.Approved ||
            contract.Status != ContractStatus.Active ||
            deposit.VerifiedIncomingAllocationTotal is not decimal held ||
            held <= 0m || held != contract.DepositAmount ||
            deposit.RecordedCompletedOutgoingTotal != 0m ||
            deposit.Blockers.Any(x => x is not
                "FinalInvoiceDebtNotReconciled" and
                not "DepositDeductionPolicyNotApproved" and
                not "RefundAuthorizationAndHandoverMissing"))
            blockers.Add("DepositLedgerNotVerifiedOrMoveOutNotApproved");

        if (await _decisions.AnyAsync(x => x.ContractId == contract.Id) ||
            await _closeoutApprovals.AnyAsync(x => x.ContractId == contract.Id))
            blockers.Add("ExistingDepositDispositionOrApprovalRequiresReview");

        var monthly = await _monthly.GetListAsync(x =>
            x.ContractId == contract.Id);
        var final = await _final.GetListAsync(x =>
            x.ContractId == contract.Id);
        var postings = await _postings.GetListAsync(x =>
            x.ContractId == contract.Id);
        if (final.Count != 0 ||
            postings.Any(x => x.Kind != OfficialInvoiceKind.Monthly))
            blockers.Add("FinalInvoiceNeedsSeparateLineClassification");

        var ids = monthly.Select(x => x.Id).ToArray();
        if (monthly.Count == 0 || postings.Count != monthly.Count ||
            monthly.Select(x => (x.BillingYear, x.BillingMonth))
                .Distinct().Count() != monthly.Count ||
            postings.Select(x => x.IssuedInvoiceId).Distinct().Count() !=
                postings.Count ||
            postings.Any(x => !ids.Contains(x.IssuedInvoiceId)))
            blockers.Add("OfficialMonthlyInvoiceCoverageIncomplete");

        // Do not query only posted receipts: this would silently hide a
        // completed, pending, orphaned, or soft-deleted payment, leaving a
        // fabricated "fully unpaid" category balance.
        List<Payment> payments;
        using (_softDelete.Disable())
        {
            payments = await _payments.GetListAsync(x =>
                x.ContractId == contract.Id &&
                x.Purpose == PaymentPurpose.InvoicePayment);
        }
        var paymentIds = payments.Select(x => x.Id).ToArray();
        var monthlyAllocations = await _allocations.GetListAsync(x =>
            x.ContractId == contract.Id || ids.Contains(x.IssuedInvoiceId) ||
            paymentIds.Contains(x.PaymentId));
        var finalAllocations = await _finalAllocations.GetListAsync(x =>
            x.ContractId == contract.Id || paymentIds.Contains(x.PaymentId));
        var receiptReviews = await _receiptReviews.GetListAsync(x =>
            x.ContractId == contract.Id);
        var adjustments = await _adjustments.GetListAsync(x =>
            x.ContractId == contract.Id);
        var adjustmentDecisions = await _adjustmentDecisions.GetListAsync(x =>
            x.ContractId == contract.Id);
        var lineAllocations = await _lineAllocations.GetListAsync(x =>
            x.ContractId == contract.Id);

        // Any foreign-journal duplicate, missing original source or a
        // final-invoice payment requires its own independently reviewed path.
        var monthPaymentIds = monthlyAllocations.Select(x => x.PaymentId).ToArray();
        if (finalAllocations.Count != 0 ||
            monthlyAllocations.Count != payments.Count ||
            monthPaymentIds.Distinct().Count() != monthPaymentIds.Length ||
            payments.Any(x => !monthPaymentIds.Contains(x.Id)) ||
            monthlyAllocations.Any(x => !ids.Contains(x.IssuedInvoiceId)) ||
            lineAllocations.Any(x => !postings.Any(p =>
                p.Id == x.ReceivablePostingId)))
            blockers.Add("InvoicePaymentsNeedVerifiedPerLineAllocation");
        if (adjustments.Count != 0 || adjustmentDecisions.Count != 0)
            blockers.Add("InvoiceCorrectionsNeedPerLineAllocation");

        // This is a contract-wide all-or-nothing proof. If ANY history
        // remains unresolved, return no money proposal or false clearance.
        if (blockers.Count != 0)
            return Blocked(requestId, contract.Id, blockers);

        var claims = new List<ContractDepositOffsetClaim>();
        var originalPayments = payments.ToDictionary(x => x.Id);
        foreach (var issue in monthly.OrderBy(x => x.BillingYear)
                     .ThenBy(x => x.BillingMonth))
        {
            var posting = postings.Single(x =>
                x.IssuedInvoiceId == issue.Id);
            var candidate = await _candidates.GetAsync(issue.CandidateId);
            var review = await _reviews.GetAsync(issue.ReviewId);
            // Convert stored journals back into actual bank receipt evidence.
            // The conversion itself verifies the immutable original Payment.
            var receiptEvidence = monthlyAllocations
                .Where(x => x.IssuedInvoiceId == issue.Id)
                .Select(x => OfficialInvoiceReceiptEvidence.FromMonthly(
                    x, originalPayments[x.PaymentId])).ToArray();
            var bankReviews = receiptReviews
                .Where(x => x.ReceivablePostingId == posting.Id).ToArray();
            var lineEvidence = lineAllocations
                .Where(x => x.ReceivablePostingId == posting.Id).ToArray();
            var result = ContractDepositOfficialMonthlyClaimBridge.Assess(
                posting, issue, candidate, review, asOfDate,
                receiptEvidence, bankReviews,
                Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
                Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(),
                lineEvidence);
            if (!result.CanPreview)
                blockers.AddRange(result.Blockers);
            else
                claims.AddRange(result.Claims);
        }

        if (claims.Select(x => x.ClaimId).Distinct().Count() != claims.Count)
            blockers.Add("DuplicateContractDebtClaim");

        if (blockers.Count != 0)
            return Blocked(requestId, contract.Id, blockers);

        // No persisted independent Owner dispute/offset sign-off exists.
        // Returning official line facts must NOT be interpreted as a
        // signed decision to seize cọc or post a new financial entry.
        return new ContractDepositOfficialOffsetRead(
            requestId, contract.Id, deposit.VerifiedIncomingAllocationTotal,
            claims.AsReadOnly(), Array.Empty<string>());
    }

    private static ContractDepositOfficialOffsetRead Blocked(
        Guid requestId, Guid contractId, IEnumerable<string> reasons)
        => new(requestId, contractId, null,
            Array.Empty<ContractDepositOffsetClaim>(),
            reasons.Distinct(StringComparer.Ordinal).ToArray());
}

/// <summary>
/// Verified evidence is NOT consent, a bank receipt, a dispute finding or an
/// Owner-approved allocation. Real money action remains strictly disabled.
/// </summary>
public sealed record ContractDepositOfficialOffsetRead(
    Guid MoveOutRequestId,
    Guid ContractId,
    decimal? VerifiedHeldVndForReview,
    IReadOnlyList<ContractDepositOffsetClaim> OfficialUnpaidMonthlyClaims,
    IReadOnlyList<string> Blockers)
{
    public bool EvidenceReviewable => Blockers.Count == 0;
    public bool CanPostOffset => false;
    public bool CanRefund => false;
}
