using Volo.Abp.Modularity;

namespace BuildingManagement;

/* Inherit from this class for your domain layer tests.
 * See SampleManager_Tests for example.
 */
public abstract class BuildingManagementDomainTestBase<TStartupModule> : BuildingManagementTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
