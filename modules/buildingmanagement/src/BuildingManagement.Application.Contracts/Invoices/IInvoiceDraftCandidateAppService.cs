using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Invoices;

/// <summary>
/// Preview only. Creation, issuance, payment collection and final
/// settlement workflows are deliberately not exposed here.
/// </summary>
public interface IInvoiceDraftCandidateAppService : IApplicationService
{
    Task<InvoiceDraftCandidateDto> GetMonthlyCandidateAsync(
        Guid contractId, int billingYear, int billingMonth);
}
