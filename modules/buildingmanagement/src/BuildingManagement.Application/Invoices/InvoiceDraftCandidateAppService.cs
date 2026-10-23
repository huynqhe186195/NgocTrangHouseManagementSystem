using System;
using System.Collections.Generic;
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
/// Phase 2G.3A.1: contract-bound, read-only invoice candidate.
/// Never writes invoice/payment tables, never issues a bill, never
/// allocates a room's whole-month usage across multiple tenants.
/// </summary>
public class InvoiceDraftCandidateAppService
    : ApplicationService, IInvoiceDraftCandidateAppService
{
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IUtilityChargePreviewAppService _charges;

    public InvoiceDraftCandidateAppService(
        IRepository<Contract, Guid> contracts,
        IUtilityChargePreviewAppService charges)
    {
        _contracts = contracts;
        _charges = charges;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<InvoiceDraftCandidateDto> GetMonthlyCandidateAsync(
        Guid contractId, int billingYear, int billingMonth)
    {
        if (contractId == Guid.Empty)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);
        if (billingYear is < 1 or > 9999)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingYear);
        if (billingMonth is < 1 or > 12)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingMonth);

        var contract = await _contracts.FindAsync(contractId);
        if (contract is null)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);

        var start = new DateTime(billingYear, billingMonth, 1);
        var end = new DateTime(
            billingYear, billingMonth, DateTime.DaysInMonth(billingYear, billingMonth));

        var blockers = new List<string>();
        if (contract.Status != ContractStatus.Active)
            blockers.Add("ContractNotActive");

        // Conservative: until split-tenancy and move-out allocation policies
        // are approved, a room-month belongs to a contract only if its
        // effective occupancy covers the ENTIRE calendar month.
        var wholeMonthOwned =
            contract.StartDate.Date <= start &&
            (!contract.EndDate.HasValue || contract.EndDate.Value.Date >= end) &&
            (!contract.ActualEndedAt.HasValue || contract.ActualEndedAt.Value.Date >= end);

        if (!wholeMonthOwned)
            blockers.Add("ContractDoesNotCoverWholeMonth");

        // An unexpected second active contract for the same room and month
        // makes whole-room utility attribution unsafe.
        if (await _contracts.AnyAsync(x =>
                x.Id != contract.Id &&
                x.RoomId == contract.RoomId &&
                (x.Status == ContractStatus.Active ||
                 (x.Status == ContractStatus.Ended && x.ActualEndedAt.HasValue)) &&
                x.StartDate <= end &&
                (!x.EndDate.HasValue || x.EndDate.Value >= start) &&
                (!x.ActualEndedAt.HasValue || x.ActualEndedAt.Value >= start)))
            blockers.Add("OverlappingActiveContractInRoom");

        var lines = new List<InvoiceDraftUtilityLineSnapshot>();
        foreach (var utilityType in Enum.GetValues<UtilityType>())
        {
            MonthlyChargePreviewDto preview;
            try
            {
                preview = await _charges.GetMonthlyChargePreviewAsync(
                    contract.RoomId, utilityType, billingYear, billingMonth);
            }
            catch (BusinessException ex)
                when (ex.Code == UtilityUsagePreviewErrorCodes.HistoryRequiresReview)
            {
                // Keep the candidate fail-closed. Never manufacture a zero
                // quantity if an audit cannot reconstruct meter history.
                throw new BusinessException(
                    UtilityUsagePreviewErrorCodes.HistoryRequiresReview)
                    .WithData("ContractId", contractId);
            }

            if (preview.ProvisionalAmount is null)
                blockers.Add(utilityType + ":RateOrUsageNotReady");

            var segments = new List<InvoiceDraftMeterSegmentSnapshot>();
            foreach (var source in preview.UsagePreview.Segments)
            {
                if (!Enum.TryParse<MeterUsageSourceType>(
                        source.SourceType, ignoreCase: false, out var sourceType) ||
                    !Enum.IsDefined(typeof(MeterUsageSourceType), sourceType))
                    throw new BusinessException(
                        UtilityUsagePreviewErrorCodes.HistoryRequiresReview);

                segments.Add(new InvoiceDraftMeterSegmentSnapshot(
                    source.SourceId, source.UtilityMeterId, sourceType,
                    source.RecordedDate, source.PreviousReading,
                    source.CurrentReading));
            }

            lines.Add(new InvoiceDraftUtilityLineSnapshot(
                utilityType,
                preview.UtilityRateId,
                preview.RateStatus,
                preview.RecordedUsage,
                preview.UnitPrice,
                wholeMonthOwned && contract.Status == ContractStatus.Active &&
                !blockers.Contains("OverlappingActiveContractInRoom")
                    ? preview.ProvisionalAmount : null,
                segments,
                preview.UsagePreview.CoverageIssues));
        }

        // Make detached, defensive copies of rate, usage and source evidence.
        // The snapshot is intentionally in-memory only.
        var snapshot = InvoiceDraftSnapshot.Capture(
            contract.Id, contract.RoomId, billingYear, billingMonth, lines);

        return new InvoiceDraftCandidateDto
        {
            ContractId = snapshot.ContractId,
            RoomId = snapshot.RoomId,
            BillingYear = snapshot.BillingYear,
            BillingMonth = snapshot.BillingMonth,
            Blockers = blockers,
            ProvisionalTotal = blockers.Count == 0
                ? snapshot.ProvisionalTotal : null,
            UtilityLines = snapshot.UtilityLines.Select(x =>
                new InvoiceDraftUtilityLineCandidateDto
                {
                    UtilityType = x.UtilityType,
                    UtilityRateId = x.UtilityRateId,
                    RateStatus = x.RateStatus,
                    RecordedQuantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    ProvisionalAmount = x.ProvisionalAmount,
                    CoverageIssues = x.CoverageIssues.ToList(),
                    MeterSegments = x.MeterSegments.Select(m => new MeterUsageSegmentDto
                    {
                        SourceId = m.SourceId,
                        UtilityMeterId = m.UtilityMeterId,
                        SourceType = m.SourceType.ToString(),
                        RecordedDate = m.RecordedDate,
                        PreviousReading = m.PreviousReading,
                        CurrentReading = m.CurrentReading,
                        Usage = m.Quantity
                    }).ToList()
                }).ToList()
        };
    }
}
