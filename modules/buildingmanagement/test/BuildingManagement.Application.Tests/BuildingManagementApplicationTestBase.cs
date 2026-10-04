using Volo.Abp.Modularity;

namespace BuildingManagement;

/* Inherit from this class for your application layer tests.
 * See SampleAppService_Tests for example.
 */
public abstract class BuildingManagementApplicationTestBase<TStartupModule> : BuildingManagementTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
