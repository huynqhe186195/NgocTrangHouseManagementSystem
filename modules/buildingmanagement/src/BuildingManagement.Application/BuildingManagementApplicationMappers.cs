using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Riok.Mapperly.Abstractions;
using Volo.Abp.Mapperly;

namespace BuildingManagement;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class BuildingManagementApplicationMapper
    : MapperBase<Building, BuildingDto>
{
    public override partial BuildingDto Map(Building source);

    public override partial void Map(
        Building source,
        BuildingDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class FloorToFloorDtoMapper
    : MapperBase<Floor, FloorDto>
{
    public override partial FloorDto Map(Floor source);

    public override partial void Map(
        Floor source,
        FloorDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class RoomToRoomDtoMapper
    : MapperBase<Room, RoomDto>
{
    public override partial RoomDto Map(Room source);

    public override partial void Map(
        Room source,
        RoomDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class TenantToTenantDtoMapper
    : MapperBase<Tenant, TenantDto>
{
    public override partial TenantDto Map(
        Tenant source);

    public override partial void Map(
        Tenant source,
        TenantDto destination);
}