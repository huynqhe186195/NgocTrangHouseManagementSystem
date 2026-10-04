using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Http.Client;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace BuildingManagement;

[DependsOn(
    typeof(BuildingManagementApplicationContractsModule),
    typeof(AbpHttpClientModule))]
public class BuildingManagementHttpApiClientModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpClientProxies(
            typeof(BuildingManagementApplicationContractsModule).Assembly,
            BuildingManagementRemoteServiceConsts.RemoteServiceName
        );

        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<BuildingManagementHttpApiClientModule>();
        });

    }
}
