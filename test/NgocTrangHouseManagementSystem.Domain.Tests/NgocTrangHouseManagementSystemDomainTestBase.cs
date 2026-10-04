using Volo.Abp.Modularity;

namespace NgocTrangHouseManagementSystem;

/* Inherit from this class for your domain layer tests. */
public abstract class NgocTrangHouseManagementSystemDomainTestBase<TStartupModule> : NgocTrangHouseManagementSystemTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
