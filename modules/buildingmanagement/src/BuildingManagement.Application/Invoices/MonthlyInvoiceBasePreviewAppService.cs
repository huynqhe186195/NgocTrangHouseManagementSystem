using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Utilities;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Phase 2G.3: a permission-neutral internal calculation used ONLY after
/// building-scoped authorization. Computes a full-month rent + utilities
/// preview while making missing service charge policies explicit.
/// </summary>
public static class MonthlyInvoiceBasePreviewCalculator
{
    public static MonthlyInvoiceBasePreviewDto Assess(
        InvoiceDraftCandidateDto candidate, decimal monthlyRent)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var report = new MonthlyInvoiceBasePreviewDto
        {
            ContractId = candidate.ContractId,
            RoomId = candidate.RoomId,
            BillingYear = candidate.BillingYear,
            BillingMonth = candidate.BillingMonth,
            KnownFullMonthRentAmount = monthlyRent
        };
        if (candidate.Blockers is null || candidate.UtilityLines is null)
            throw new InvalidOperationException("Invoice candidate has invalid source evidence.");

        report.Blockers.AddRange(candidate.Blockers);
        if (candidate.ContractId == Guid.Empty || candidate.RoomId == Guid.Empty ||
            candidate.BillingYear is < 1 or > 9999 ||
            candidate.BillingMonth is < 1 or > 12)
            report.Blockers.Add("InvalidInvoiceCandidateIdentity");
        if (monthlyRent <= 0m || decimal.Truncate(monthlyRent) != monthlyRent)
            report.Blockers.Add("InvalidOrUnroundedMonthlyRent");

        if (candidate.UtilityLines.Count != 2 ||
            candidate.UtilityLines.Select(x => x.UtilityType).Distinct().Count() != 2 ||
            !candidate.UtilityLines.Any(x => x.UtilityType == UtilityType.Electricity) ||
            !candidate.UtilityLines.Any(x => x.UtilityType == UtilityType.Water))
            report.Blockers.Add("BothUtilityLinesRequired");

        bool usable = report.Blockers.Count == 0;
        foreach (var line in candidate.UtilityLines)
        {
            if (line is null ||
                line.RateStatus != "SingleRateWholeMonth" ||
                line.UtilityRateId.GetValueOrDefault() == Guid.Empty ||
                line.RecordedQuantity < 0m ||
                line.UnitPrice is not decimal price || price <= 0m ||
                line.ProvisionalAmount is not decimal amount || amount < 0m ||
                line.CoverageIssues is null || line.CoverageIssues.Count != 0 ||
                line.MeterSegments is null || line.MeterSegments.Count == 0 ||
                amount != line.RecordedQuantity * price ||
                decimal.Truncate(amount) != amount)
            {
                usable = false;
                report.Blockers.Add("UnsafeOrUnroundedUtilityLine");
                continue;
            }
            if (line.UtilityType == UtilityType.Electricity)
                report.ProvisionalElectricityAmount = amount;
            else if (line.UtilityType == UtilityType.Water)
                report.ProvisionalWaterAmount = amount;
        }

        if (!usable || candidate.ProvisionalTotal is not decimal utilities ||
            report.ProvisionalElectricityAmount is not decimal electricity ||
            report.ProvisionalWaterAmount is not decimal water ||
            electricity + water != utilities ||
            decimal.Truncate(utilities) != utilities)
            report.Blockers.Add("MonthlyUtilitiesNotFullyVerified");
        else
            report.ProvisionalRentAndUtilitiesAmount = monthlyRent + utilities;

        // Section 5.1 requires Internet, trash, parking and other services
        // to follow a real contract/rate policy. Their absence from the
        // utility candidate is UNKNOWN, not a proof of no charge.
        // This blocker deliberately does NOT suppress the known base preview.
        report.Blockers.Add("OtherServiceFeesRequireReviewBeforeIssuance");
        return report;
    }
}

/// <summary>
/// Internal read-only monthly rent + electricity + water snapshot preview.
/// No Invoice/Payment write and no network route. Outer UOWs must be
/// REPEATABLE READ so the rent/contract and existing utility candidate
/// reflect one consistent PostgreSQL snapshot.
/// </summary>
[RemoteService(IsEnabled = false)]
public class MonthlyInvoiceBasePreviewAppService : ApplicationService
{
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IInvoiceDraftCandidateAppService _candidates;
    private readonly IInvoiceDraftReadIsolationGuard _isolationGuard;

    public MonthlyInvoiceBasePreviewAppService(
        IRepository<Contract, Guid> contracts,
        IInvoiceDraftCandidateAppService candidates,
        IInvoiceDraftReadIsolationGuard isolationGuard)
    {
        _contracts = contracts;
        _candidates = candidates;
        _isolationGuard = isolationGuard;
    }

    [UnitOfWork(true, IsolationLevel.RepeatableRead)]
    public virtual async Task<MonthlyInvoiceBasePreviewDto> GetAsync(
        Guid contractId, int billingYear, int billingMonth)
    {
        await _isolationGuard.RequireRepeatableReadAsync();
        var contract = contractId == Guid.Empty
            ? null
            : await _contracts.FindAsync(contractId);
        if (contract is null)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);
        var candidate = await _candidates.GetMonthlyCandidateAsync(
            contractId, billingYear, billingMonth);
        if (candidate.ContractId != contract.Id ||
            candidate.RoomId != contract.RoomId)
            throw new InvalidOperationException(
                "Monthly source candidate no longer matches the contract and room.");

        return MonthlyInvoiceBasePreviewCalculator.Assess(
            candidate, contract.MonthlyRent);
    }
}
