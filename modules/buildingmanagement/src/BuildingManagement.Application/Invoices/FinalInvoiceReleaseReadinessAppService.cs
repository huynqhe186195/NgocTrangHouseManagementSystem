using System;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only final invoice issue readiness assembled from actual
/// persisted evidence and audit services within one read transaction.
/// No Issue, Post, Approve, Pay, Refund or Contract.End operation exists.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalInvoiceReleaseReadinessAppService : ApplicationService
{
    private readonly FinalInvoiceIssuePreflightAppService _preflight;
    private readonly IRepository<FinalInvoiceIssueEvidenceRecord, Guid> _evidence;
    private readonly IRepository<InvoiceIssuanceReviewTicket, Guid> _reviews;
    private readonly FinalInvoiceIssueEvidenceDriftAppService _drift;
    private readonly InvoicePaymentAllocationLedgerReviewAppService _allocationLedger;

    public FinalInvoiceReleaseReadinessAppService(
        FinalInvoiceIssuePreflightAppService preflight,
        IRepository<FinalInvoiceIssueEvidenceRecord, Guid> evidence,
        IRepository<InvoiceIssuanceReviewTicket, Guid> reviews,
        FinalInvoiceIssueEvidenceDriftAppService drift,
        InvoicePaymentAllocationLedgerReviewAppService allocationLedger)
    {
        _preflight = preflight;
        _evidence = evidence;
        _reviews = reviews;
        _drift = drift;
        _allocationLedger = allocationLedger;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<FinalInvoiceReleaseReadiness> GetAsync(
        Guid moveOutRequestId)
    {
        if (moveOutRequestId == Guid.Empty)
            throw new ArgumentException(
                "Move-out request ID required.", nameof(moveOutRequestId));

        // The server-owned preflight establishes contract and draft scope.
        // Never accept these resource identifiers from a client DTO.
        var preflight = await _preflight.GetAsync(moveOutRequestId);
        var archives = await _evidence.GetListAsync(x =>
            x.MoveOutRequestId == moveOutRequestId);
        var reviewTickets = await _reviews.GetListAsync(x =>
            x.MoveOutRequestId == moveOutRequestId);

        FinalInvoiceIssueEvidenceDriftReview? drift = null;
        if (archives.Count == 1)
            drift = await _drift.GetAsync(archives[0].Id);

        var allocation = await _allocationLedger.GetAsync(
            preflight.ContractId);

        return FinalInvoiceReleaseReadinessEvaluator.Assess(
            preflight, archives, reviewTickets, drift, allocation);
    }
}
