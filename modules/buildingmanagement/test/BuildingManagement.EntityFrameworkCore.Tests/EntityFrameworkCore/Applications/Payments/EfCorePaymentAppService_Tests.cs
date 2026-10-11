namespace BuildingManagement.EntityFrameworkCore
    .Applications.Payments;

public class EfCorePaymentAppService_Tests
    : global::BuildingManagement.Payments
        .PaymentAppService_Tests<
            BuildingManagementEntityFrameworkCoreTestModule>
{
}