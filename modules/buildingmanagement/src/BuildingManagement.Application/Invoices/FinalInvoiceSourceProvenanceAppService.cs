using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal-only provenance audit. Resolves the original meter/rate rows
/// from archived source IDs and the room's persisted building relationship.
/// Historical soft deletions are loaded for anomaly detection. It neither
/// mutates original readings nor uses current tariffs to reprice a draft.
/// </summary>
[RemoteService(IsEnabled = false)]
public class FinalInvoiceSourceProvenanceAppService : ApplicationService
{
    private readonly IRepository<FinalInvoiceIssueEvidenceRecord, Guid> _archives;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<MeterReading, Guid> _readings;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public FinalInvoiceSourceProvenanceAppService(
        IRepository<FinalInvoiceIssueEvidenceRecord, Guid> archives,
        IRepository<Room, Guid> rooms,
        IRepository<Floor, Guid> floors,
        IRepository<MeterReading, Guid> readings,
        IRepository<UtilityMeter, Guid> meters,
        IRepository<UtilityRate, Guid> rates,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _archives = archives;
        _rooms = rooms;
        _floors = floors;
        _readings = readings;
        _meters = meters;
        _rates = rates;
        _softDeleteFilter = softDeleteFilter;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<FinalInvoiceSourceProvenanceReview> GetAsync(
        Guid stagedEvidenceId)
    {
        if (stagedEvidenceId == Guid.Empty)
            throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        var archive = await _archives.FindAsync(stagedEvidenceId)
            ?? throw new BusinessException(InvoiceDraftErrorCodes.CandidateNotReady);

        FinalInvoiceUtilityEvidence[] utilities;
        try
        {
            archive.VerifyStoredEvidence();
            using var json = JsonDocument.Parse(archive.EvidenceJson);
            utilities = JsonSerializer.Deserialize<FinalInvoiceUtilityEvidence[]>(
                json.RootElement.GetProperty("Utilities").GetRawText())
                ?? Array.Empty<FinalInvoiceUtilityEvidence>();
        }
        catch (Exception error) when (error is System.IO.InvalidDataException
            or JsonException or InvalidOperationException)
        {
            // Assessment reports a corrupt archive without exposing an
            // uncaught parsing error or returning a partial green result.
            return FinalInvoiceSourceProvenanceEvaluator.Assess(
                archive, Guid.Empty, Array.Empty<MeterReading>(),
                Array.Empty<UtilityMeter>(), Array.Empty<UtilityRate>());
        }

        var room = await _rooms.FindAsync(archive.RoomId);
        var floor = room is null ? null : await _floors.FindAsync(room.FloorId);
        var buildingId = floor?.BuildingId ?? Guid.Empty;

        var readingIds = utilities.SelectMany(x => x.Sources)
            .Where(x => x.SourceType == MeterUsageSourceType.MonthlyReading)
            .Select(x => x.SourceId).Distinct().ToArray();
        var meterIds = utilities.SelectMany(x => x.Sources)
            .Select(x => x.UtilityMeterId).Distinct().ToArray();
        var rateIds = utilities.Select(x => x.UtilityRateId)
            .Distinct().ToArray();

        List<MeterReading> readings;
        List<UtilityMeter> meters;
        List<UtilityRate> rates;
        using (_softDeleteFilter.Disable())
        {
            readings = readingIds.Length == 0
                ? new List<MeterReading>()
                : await _readings.GetListAsync(x => readingIds.Contains(x.Id));
            meters = meterIds.Length == 0
                ? new List<UtilityMeter>()
                : await _meters.GetListAsync(x => meterIds.Contains(x.Id));
            rates = rateIds.Length == 0
                ? new List<UtilityRate>()
                : await _rates.GetListAsync(x => rateIds.Contains(x.Id));
        }

        return FinalInvoiceSourceProvenanceEvaluator.Assess(
            archive, buildingId, readings, meters, rates);
    }
}
