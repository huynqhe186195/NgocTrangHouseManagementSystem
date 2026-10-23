using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Invoices;

/// <summary>
/// Internal draft storage only. No public remote route is exposed yet:
/// operator permissions and production migrations are still pending.
/// </summary>
public interface IInvoiceDraftStorageAppService : IApplicationService
{
    Task<InvoiceDraftDto> CreateAsync(CreateInvoiceDraftDto input);
    Task<InvoiceDraftDto> GetAsync(Guid id);
}
