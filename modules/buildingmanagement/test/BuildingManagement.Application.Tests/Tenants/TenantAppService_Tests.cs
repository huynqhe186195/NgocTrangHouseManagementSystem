using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Validation;
using Xunit;

namespace BuildingManagement.Tenants;

public abstract class TenantAppService_Tests<TStartupModule>
    : BuildingManagementApplicationTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{
    private readonly ITenantAppService _tenantAppService;

    private static int _sequence;

    protected TenantAppService_Tests()
    {
        _tenantAppService =
            GetRequiredService<ITenantAppService>();
    }

    private static (
        string PhoneNumber,
        string CitizenId
    ) NewIdentity()
    {
        var value =
            Interlocked.Increment(
                ref _sequence
            );

        return (
            $"099{value:D7}",
            $"088{value:D9}"
        );
    }

    private static CreateTenantDto
        NewValidCreateInput()
    {
        var identity =
            NewIdentity();

        return new CreateTenantDto
        {
            FullName =
                $"Tenant {Guid.NewGuid():N}",

            PhoneNumber =
                identity.PhoneNumber,

            IdentityType =
                IdentityType.CitizenId,

            IdentityNumber =
                identity.CitizenId
        };
    }

    private static UpdateTenantDto
        NewValidUpdateInput()
    {
        var identity =
            NewIdentity();

        return new UpdateTenantDto
        {
            FullName =
                $"Updated Tenant {Guid.NewGuid():N}",

            PhoneNumber =
                identity.PhoneNumber,

            IdentityType =
                IdentityType.CitizenId,

            IdentityNumber =
                identity.CitizenId
        };
    }

    // T1
    [Fact]
    public async Task
        Should_Create_Tenant_With_Valid_Identity()
    {
        var input =
            NewValidCreateInput();

        var result =
            await _tenantAppService
                .CreateAsync(input);

        result.Id.ShouldNotBe(
            Guid.Empty
        );

        result.PhoneNumber.ShouldBe(
            input.PhoneNumber
        );

        result.IdentityType.ShouldBe(
            IdentityType.CitizenId
        );

        result.IdentityNumber.ShouldBe(
            input.IdentityNumber
        );
    }

    // T2
    [Fact]
    public async Task
        Should_Normalize_Vietnamese_Phone_Number()
    {
        var input =
            NewValidCreateInput();

        var expectedPhone =
            input.PhoneNumber;

        input.PhoneNumber =
            "+84" + expectedPhone[1..];

        var result =
            await _tenantAppService
                .CreateAsync(input);

        result.PhoneNumber.ShouldBe(
            expectedPhone
        );
    }

    // T3
    [Fact]
    public async Task
        Should_Throw_When_Phone_Number_Is_Invalid()
    {
        var input =
            NewValidCreateInput();

        input.PhoneNumber =
            "123ABC";

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidTenantPhoneNumber
        );
    }

    // T4
    [Fact]
    public async Task
        Should_Throw_When_CitizenId_Length_Is_Invalid()
    {
        var input =
            NewValidCreateInput();

        input.IdentityNumber =
            "0123456789";

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidTenantCitizenId
        );
    }

    // T5
    [Fact]
    public async Task
        Should_Throw_When_CitizenId_Contains_Non_Digit()
    {
        var input =
            NewValidCreateInput();

        input.IdentityNumber =
            "01234567890A";

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .InvalidTenantCitizenId
        );
    }

    // T6
    [Fact]
    public async Task
        Should_Throw_When_CitizenId_Already_Exists()
    {
        var firstInput =
            NewValidCreateInput();

        await _tenantAppService
            .CreateAsync(firstInput);

        var secondInput =
            NewValidCreateInput();

        secondInput.IdentityNumber =
            firstInput.IdentityNumber;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(
                            secondInput
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantIdentityAlreadyExists
        );
    }

    // T7
    [Fact]
    public async Task
        Should_Throw_When_IdentityType_Is_Not_CitizenId()
    {
        var input =
            NewValidCreateInput();

        input.IdentityType =
            (IdentityType)999;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantIdentityMustBeCitizenId
        );
    }

    // T8
    [Fact]
    public async Task
        Should_Update_Tenant_Identity()
    {
        var created =
            await _tenantAppService
                .CreateAsync(
                    NewValidCreateInput()
                );

        var updateInput =
            NewValidUpdateInput();

        var updated =
            await _tenantAppService
                .UpdateAsync(
                    created.Id,
                    updateInput
                );

        updated.Id.ShouldBe(
            created.Id
        );

        updated.FullName.ShouldBe(
            updateInput.FullName
        );

        updated.PhoneNumber.ShouldBe(
            updateInput.PhoneNumber
        );

        updated.IdentityType.ShouldBe(
            IdentityType.CitizenId
        );

        updated.IdentityNumber.ShouldBe(
            updateInput.IdentityNumber
        );
    }

    // T9
    [Fact]
    public async Task
        Should_Throw_When_Update_Uses_Another_Tenants_CitizenId()
    {
        var firstInput =
            NewValidCreateInput();

        var first =
            await _tenantAppService
                .CreateAsync(firstInput);

        var second =
            await _tenantAppService
                .CreateAsync(
                    NewValidCreateInput()
                );

        var updateInput =
            NewValidUpdateInput();

        updateInput.IdentityNumber =
            firstInput.IdentityNumber;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .UpdateAsync(
                            second.Id,
                            updateInput
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantIdentityAlreadyExists
        );

        first.Id.ShouldNotBe(
            second.Id
        );
    }

    // T10
    [Fact]
    public async Task
        Should_Require_Phone_Number()
    {
        var input =
            NewValidCreateInput();

        input.PhoneNumber =
            null!;

        var exception =
            await Assert.ThrowsAsync<
                AbpValidationException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.ValidationErrors
            .ShouldContain(
                x =>
                    x.MemberNames.Contains(
                        nameof(
                            CreateTenantDto
                                .PhoneNumber
                        )
                    )
            );
    }

    // T11
    [Fact]
    public async Task
        Should_Require_CitizenId()
    {
        var input =
            NewValidCreateInput();

        input.IdentityNumber =
            null!;

        var exception =
            await Assert.ThrowsAsync<
                AbpValidationException>(
                () =>
                    _tenantAppService
                        .CreateAsync(input)
            );

        exception.ValidationErrors
            .ShouldContain(
                x =>
                    x.MemberNames.Contains(
                        nameof(
                            CreateTenantDto
                                .IdentityNumber
                        )
                    )
            );
    }

    // T12
    [Fact]
    public async Task
        Should_Not_Reuse_CitizenId_Of_Soft_Deleted_Tenant()
    {
        var firstInput =
            NewValidCreateInput();

        var first =
            await _tenantAppService
                .CreateAsync(firstInput);

        await _tenantAppService
            .DeleteAsync(first.Id);

        var secondInput =
            NewValidCreateInput();

        secondInput.IdentityNumber =
            firstInput.IdentityNumber;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(
                            secondInput
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantIdentityAlreadyExists
        );
    }

    // T13
    [Fact]
    public async Task
        Should_Throw_When_PhoneNumber_Already_Exists()
    {
        var firstInput =
            NewValidCreateInput();

        await _tenantAppService
            .CreateAsync(firstInput);

        var secondInput =
            NewValidCreateInput();

        secondInput.PhoneNumber =
            firstInput.PhoneNumber;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .CreateAsync(secondInput)
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantPhoneNumberAlreadyExists
        );
    }

    // T14
    [Fact]
    public async Task
        Should_Throw_When_Update_Uses_Another_Tenants_PhoneNumber()
    {
        var firstInput =
            NewValidCreateInput();

        await _tenantAppService
            .CreateAsync(firstInput);

        var second =
            await _tenantAppService
                .CreateAsync(
                    NewValidCreateInput()
                );

        var updateInput =
            NewValidUpdateInput();

        updateInput.PhoneNumber =
            firstInput.PhoneNumber;

        var exception =
            await Assert.ThrowsAsync<
                BusinessException>(
                () =>
                    _tenantAppService
                        .UpdateAsync(
                            second.Id,
                            updateInput
                        )
            );

        exception.Code.ShouldBe(
            BuildingManagementErrorCodes
                .TenantPhoneNumberAlreadyExists
        );
    }
}