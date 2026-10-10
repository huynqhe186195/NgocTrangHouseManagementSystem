using BuildingManagement.Contracts;

namespace BuildingManagement.EntityFrameworkCore
    .Applications.Contracts;

public class EfCoreContractRenewalHoldAppService_Tests
    : ContractRenewalHoldAppService_Tests<
        BuildingManagementEntityFrameworkCoreTestModule>
{
}