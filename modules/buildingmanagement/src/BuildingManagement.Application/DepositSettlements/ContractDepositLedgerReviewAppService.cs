using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Internal review of existing allocation/refund/transfer journal records.
/// Loads cross-contract references for the same payment to surface a
/// double-spend anomaly. NEVER posts a settlement, refunds or offsets debt.
/// </summary>
[RemoteService(IsEnabled = false)]
public class ContractDepositLedgerReviewAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<DepositSettlement, Guid> _settlements;
    private readonly IRepository<Payment, Guid> _payments;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public ContractDepositLedgerReviewAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<Contract, Guid> contracts,
        IRepository<DepositSettlement, Guid> settlements,
        IRepository<Payment, Guid> payments,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _requests = requests;
        _contracts = contracts;
        _settlements = settlements;
        _payments = payments;
        _softDeleteFilter = softDeleteFilter;
    }

    public virtual async Task<ContractDepositLedgerReview> GetAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        var request = await _requests.FindAsync(requestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var contract = await _contracts.FindAsync(request.ContractId);
        var contractId = request.ContractId;
        var related = await _settlements.GetListAsync(x =>
            x.TargetContractId == contractId ||
            x.SourceContractId == contractId);
        var paymentIds = related
            .Where(x => x.PaymentId.HasValue)
            .Select(x => x.PaymentId!.Value)
            .Distinct().ToArray();

        // A completed deposit may have been allocated to a second contract.
        // Fetch the entire known allocation history for each referenced
        // payment, not only rows attached to this request's contract.
        if (paymentIds.Length != 0)
        {
            var otherAllocations = await _settlements.GetListAsync(x =>
                x.PaymentId.HasValue && paymentIds.Contains(x.PaymentId.Value));
            related = related.Concat(otherAllocations)
                .GroupBy(x => x.Id).Select(x => x.First()).ToList();
        }

        // A completed ContractDeposit may exist WITHOUT an allocation
        // journal row (legacy imports, interrupted updates or corruption).
        // Load those payments as well, including soft-deleted history.
        // Otherwise the review would incorrectly overlook cash actually
        // received simply because there is no matching settlement.
        List<Payment> receipts;
        using (_softDeleteFilter.Disable())
        {
            receipts = await _payments.GetListAsync(x =>
                (x.ContractId == contractId &&
                 x.Purpose == PaymentPurpose.ContractDeposit) ||
                paymentIds.Contains(x.Id));
        }

        return ContractDepositLedgerReviewEvaluator.Assess(
            request, contract, related, receipts);
    }
}
