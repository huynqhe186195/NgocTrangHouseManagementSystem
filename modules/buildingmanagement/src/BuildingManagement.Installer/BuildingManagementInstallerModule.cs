using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace BuildingManagement;

[DependsOn(
    typeof(AbpVirtualFileSystemModule)
    )]
public class BuildingManagementInstallerModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<BuildingManagementInstallerModule>();
        });
    }
}
