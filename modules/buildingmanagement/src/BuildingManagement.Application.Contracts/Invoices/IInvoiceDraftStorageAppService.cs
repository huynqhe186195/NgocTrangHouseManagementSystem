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

    /// <summary>
    /// Internal-only, point-in-time comparison of stored draft evidence
    /// with freshly reconstructed meter and tariff data.
    /// A true result is NOT authorization to issue or recognize debt.
    /// Future issuance must recheck atomically at finalization.
    /// </summary>
    Task<bool> HasCurrentSourceSnapshotAsync(Guid id);
}
