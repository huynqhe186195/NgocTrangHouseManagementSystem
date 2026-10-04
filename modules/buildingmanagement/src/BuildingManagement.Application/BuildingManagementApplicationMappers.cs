using BuildingManagement.Buildings;
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