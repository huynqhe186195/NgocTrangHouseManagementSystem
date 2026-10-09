using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using BuildingManagement.Utilities;
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

[Mapper(
    RequiredMappingStrategy =
        RequiredMappingStrategy.Target)]
public partial class ContractToContractDtoMapper
    : MapperBase<Contract, ContractDto>
{
    public override partial ContractDto Map(
        Contract source
    );

    public override partial void Map(
        Contract source,
        ContractDto destination
    );
}

[Mapper(
    RequiredMappingStrategy =
        RequiredMappingStrategy.Target)]
public partial class ContractTenantToContractTenantDtoMapper
    : MapperBase<ContractTenant, ContractTenantDto>
{
    public override partial ContractTenantDto Map(
        ContractTenant source
    );

    public override partial void Map(
        ContractTenant source,
        ContractTenantDto destination
    );
}

[Mapper(
    RequiredMappingStrategy =
        RequiredMappingStrategy.Target)]
public partial class UtilityRateToUtilityRateDtoMapper
    : MapperBase<UtilityRate, UtilityRateDto>
{
    public override partial UtilityRateDto Map(
        UtilityRate source
    );

    public override partial void Map(
        UtilityRate source,
        UtilityRateDto destination
    );
}

[Mapper(
    RequiredMappingStrategy =
        RequiredMappingStrategy.Target)]
public partial class UtilityMeterToUtilityMeterDtoMapper
    : MapperBase<UtilityMeter, UtilityMeterDto>
{
    public override partial UtilityMeterDto Map(
        UtilityMeter source
    );

    public override partial void Map(
        UtilityMeter source,
        UtilityMeterDto destination
    );
}

[Mapper(
    RequiredMappingStrategy =
        RequiredMappingStrategy.Target)]
public partial class MeterReadingToMeterReadingDtoMapper
    : MapperBase<MeterReading, MeterReadingDto>
{
    public override partial MeterReadingDto Map(
        MeterReading source
    );

    public override partial void Map(
        MeterReading source,
        MeterReadingDto destination
    );
}
