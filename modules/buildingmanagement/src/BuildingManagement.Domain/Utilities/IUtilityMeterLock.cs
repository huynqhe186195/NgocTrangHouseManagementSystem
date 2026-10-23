using System;
using System.Threading.Tasks;

namespace BuildingManagement.Utilities;

/// <summary>
/// Shared per-physical-meter lock for readings, replacement and maintenance.
/// Must be called inside a transactional unit of work BEFORE loading state.
/// </summary>
public interface IUtilityMeterLock
{
    Task AcquireAsync(Guid utilityMeterId);
}
