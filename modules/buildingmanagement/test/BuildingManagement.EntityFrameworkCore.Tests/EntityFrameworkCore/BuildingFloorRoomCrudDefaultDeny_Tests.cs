using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Volo.Abp.Authorization;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Directly resolves REAL registered AppServices and enforced security policy
/// on isolated SQLite. With no approved Identity/grant bootstrap, every CRUD
/// entry point must reject before creating or returning business data.
/// No production app or database is started.
/// </summary>
public sealed class BuildingFloorRoomCrudDefaultDeny_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    [Fact]
    public async Task CRUDENTRY01_All_Five_Building_Operations_Are_Default_Denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var service = GetRequiredService<IBuildingAppService>();
            var id = Guid.NewGuid();
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.CreateAsync(new CreateBuildingDto {
                    Name = "Test", Address = "SQLite isolated" }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetAsync(id));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetListAsync());
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.UpdateAsync(id, new UpdateBuildingDto {
                    Name = "Test", Address = "SQLite isolated" }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.DeleteAsync(id));
        });
    }

    [Fact]
    public async Task CRUDENTRY02_All_Five_Floor_Operations_Are_Default_Denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var service = GetRequiredService<IFloorAppService>();
            var id = Guid.NewGuid();
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.CreateAsync(new CreateFloorDto {
                    BuildingId = Guid.NewGuid(), FloorNumber = 1,
                    Name = "Test" }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetAsync(id));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetListAsync());
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.UpdateAsync(id, new UpdateFloorDto {
                    FloorNumber = 1, Name = "Test" }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.DeleteAsync(id));
        });
    }

    [Fact]
    public async Task CRUDENTRY03_All_Five_Room_Operations_Are_Default_Denied()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var service = GetRequiredService<IRoomAppService>();
            var id = Guid.NewGuid();
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.CreateAsync(new CreateRoomDto {
                    FloorId = Guid.NewGuid(), RoomNumber = "101",
                    Name = "Test", Capacity = 2, Status = RoomStatus.Available }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetAsync(id));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.GetListAsync());
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.UpdateAsync(id, new UpdateRoomDto {
                    RoomNumber = "101", Name = "Test", Capacity = 2,
                    Status = RoomStatus.Available }));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                service.DeleteAsync(id));
        });
    }
}
