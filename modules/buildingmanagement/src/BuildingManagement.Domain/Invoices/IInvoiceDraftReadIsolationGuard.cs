using System.Threading.Tasks;

namespace BuildingManagement.Invoices;

/// <summary>
/// Rejects unsafe database snapshots before reading invoice evidence.
/// Implemented by the persistence adapter, not by an application service.
/// </summary>
public interface IInvoiceDraftReadIsolationGuard
{
    Task RequireRepeatableReadAsync();
}
