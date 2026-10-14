using BuildingManagement.Tenants;

namespace BuildingManagement.EntityFrameworkCore
    .Applications.Tenants;

public class EfCoreTenantAppService_Tests
    : TenantAppService_Tests<
        BuildingManagementEntityFrameworkCoreTestModule>
{
}