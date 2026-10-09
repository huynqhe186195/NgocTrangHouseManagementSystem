using BuildingManagement.Contracts;

namespace BuildingManagement.EntityFrameworkCore.Applications.Contracts;

public class EfCoreContractAppService_Tests
    : ContractAppService_Tests<
        BuildingManagementEntityFrameworkCoreTestModule>
{
}