using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Utilities;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only persistence bridge for utility draft snapshots.
/// Deliberately not exposed as a remote HTTP API until authorization,
/// migration rollout and issuance policy are approved.
/// It never creates rent lines, posts receivables or moves deposits.
/// </summary>
[RemoteService(IsEnabled = false)]
public class InvoiceDraftStorageAppService
    : ApplicationService, IInvoiceDraftStorageAppService
{
    private readonly IContractRepository _contractLocks;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceItem, Guid> _items;
    private readonly IRepository<InvoiceItemMeterSource, Guid> _sources;
    private readonly IInvoiceDraftCandidateAppService _candidates;

    public InvoiceDraftStorageAppService(
        IContractRepository contractLocks,
        IRepository<Invoice, Guid> invoices,
        IRepository<InvoiceItem, Guid> items,
        IRepository<InvoiceItemMeterSource, Guid> sources,
        IInvoiceDraftCandidateAppService candidates)
    {
        _contractLocks = contractLocks;
        _invoices = invoices;
        _items = items;
        _sources = sources;
        _candidates = candidates;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<InvoiceDraftDto> CreateAsync(CreateInvoiceDraftDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ContractId == Guid.Empty)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);
        if (input.BillingYear is < 1 or > 9999)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingYear);
        if (input.BillingMonth is < 1 or > 12)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingMonth);

        // Shared PostgreSQL row lock: same ordering as the existing
        // contract/move-out mutators. Only after locking do we read
        // a fresh candidate and create the entire draft graph.
        var contract = await _contractLocks.FindForUpdateAsync(input.ContractId);
        if (contract is null)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);

        if (await _invoices.AnyAsync(x =>
                x.ContractId == contract.Id &&
                x.BillingYear == input.BillingYear &&
                x.BillingMonth == input.BillingMonth))
            throw new BusinessException(InvoiceDraftErrorCodes.AlreadyExists);

        var candidate = await _candidates.GetMonthlyCandidateAsync(
            contract.Id, input.BillingYear, input.BillingMonth);

        // Only server-side live meter/rate candidates are trusted. Clients
        // never submit meter counters, tariffs, room identities or amounts.
        if (candidate.ContractId != contract.Id ||
            candidate.RoomId != contract.RoomId ||
            candidate.Blockers.Count > 0 ||
            candidate.ProvisionalTotal is null ||
            candidate.UtilityLines.Count != 2 ||
            candidate.UtilityLines.Select(x => x.UtilityType).Distinct().Count() != 2 ||
            candidate.UtilityLines.Any(x => x.ProvisionalAmount is null ||
                                            x.CoverageIssues.Count != 0 ||
                                            x.MeterSegments.Count == 0))
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady)
                .WithData("Blockers", string.Join(",", candidate.Blockers));

        var lines = candidate.UtilityLines.Select(x =>
            new InvoiceDraftUtilityLineSnapshot(
                x.UtilityType, x.UtilityRateId, x.RateStatus,
                x.RecordedQuantity, x.UnitPrice, x.ProvisionalAmount,
                x.MeterSegments.Select(segment =>
                {
                    if (!Enum.TryParse<MeterUsageSourceType>(
                            segment.SourceType, out var sourceType) ||
                        !Enum.IsDefined(typeof(MeterUsageSourceType), sourceType))
                        throw new BusinessException(
                            UtilityUsagePreviewErrorCodes.HistoryRequiresReview);
                    return new InvoiceDraftMeterSegmentSnapshot(
                        segment.SourceId, segment.UtilityMeterId, sourceType,
                        segment.RecordedDate, segment.PreviousReading,
                        segment.CurrentReading);
                }), x.CoverageIssues)).ToList();

        var snapshot = InvoiceDraftSnapshot.Capture(
            contract.Id, contract.RoomId, input.BillingYear, input.BillingMonth,
            lines);
        if (snapshot.ProvisionalTotal != candidate.ProvisionalTotal)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var invoice = Invoice.CreateDraft(
            GuidGenerator.Create(), snapshot, () => GuidGenerator.Create());

        // Single UoW persists header, utility items and immutable meter
        // evidence. Nonfiltered database indexes also reject races that
        // bypass application pre-checks.
        await _invoices.InsertAsync(invoice, autoSave: true);
        return ToDto(invoice, invoice.Items, invoice.Items.SelectMany(x => x.MeterSources));
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<InvoiceDraftDto> GetAsync(Guid id)
    {
        var invoice = id == Guid.Empty ? null : await _invoices.FindAsync(id);
        if (invoice is null)
            throw new BusinessException(InvoiceDraftErrorCodes.NotFound);

        var items = await _items.GetListAsync(x => x.InvoiceId == id);
        var ids = items.Select(x => x.Id).ToArray();
        var sources = ids.Length == 0
            ? new List<InvoiceItemMeterSource>()
            : await _sources.GetListAsync(x => ids.Contains(x.InvoiceItemId));
        return ToDto(invoice, items, sources);
    }

    private static InvoiceDraftDto ToDto(
        Invoice invoice,
        IEnumerable<InvoiceItem> items,
        IEnumerable<InvoiceItemMeterSource> sources)
    {
        var allSources = sources.ToList();
        return new InvoiceDraftDto
        {
            Id = invoice.Id,
            ContractId = invoice.ContractId,
            RoomId = invoice.RoomId,
            BillingYear = invoice.BillingYear,
            BillingMonth = invoice.BillingMonth,
            Status = invoice.Status,
            ProvisionalUtilityTotal = invoice.ProvisionalUtilityTotal,
            Items = items.OrderBy(x => x.UtilityType).Select(line =>
                new InvoiceDraftItemDto
                {
                    Id = line.Id,
                    UtilityType = line.UtilityType,
                    UtilityRateId = line.UtilityRateId,
                    RateStatus = line.RateStatus,
                    Quantity = line.RecordedQuantity,
                    UnitPrice = line.UnitPrice,
                    ProvisionalAmount = line.ProvisionalAmount,
                    CoverageIssues = JsonSerializer.Deserialize<List<string>>(
                        line.CoverageIssuesJson) ?? new List<string>(),
                    Sources = allSources.Where(x => x.InvoiceItemId == line.Id)
                        .OrderBy(x => x.RecordedDate)
                        .ThenBy(x => x.SourceId)
                        .Select(source => new InvoiceDraftSourceDto
                        {
                            SourceId = source.SourceId,
                            UtilityMeterId = source.UtilityMeterId,
                            SourceType = source.SourceType.ToString(),
                            RecordedDate = source.RecordedDate,
                            PreviousReading = source.PreviousReading,
                            CurrentReading = source.CurrentReading,
                            Quantity = source.Quantity
                        }).ToList()
                }).ToList()
        };
    }
}
