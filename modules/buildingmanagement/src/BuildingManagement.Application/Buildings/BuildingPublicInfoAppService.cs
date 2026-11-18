using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Buildings;

public class BuildingPublicInfoAppService :
    ApplicationService,
    IBuildingPublicInfoAppService
{
    private readonly IRepository<BuildingPublicInfo, Guid> _infoRepository;
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly IBuildingScopedWriteBoundary _writes;

    public BuildingPublicInfoAppService(
        IRepository<BuildingPublicInfo, Guid> infoRepository,
        IBuildingCrudAccessPolicy access,
        IBuildingScopedWriteBoundary writes)
    {
        _infoRepository = infoRepository;
        _access = access;
        _writes = writes;
    }

    public async Task<BuildingPublicInfoDto?> GetAsync(Guid id)
    {
        await _access.EnsureBuildingAsync(
            id, BuildingBusinessCapability.ReadBuilding);
        var info = await _infoRepository.FindAsync(x => x.BuildingId == id);
        return info is null ? null : ToDto(info);
    }

    public async Task<BuildingPublicInfoDto> UpdateAsync(
        Guid id, UpdateBuildingPublicInfoDto input)
    {
        await _access.EnsureBuildingAsync(
            id, BuildingBusinessCapability.ManageBuilding);
        ValidateRules(input.HouseRules);
        return await _writes.ExecuteBuildingAsync(id, async () =>
        {
            var info = await _infoRepository.FindAsync(
                x => x.BuildingId == id);
            if (info is null)
            {
                info = new BuildingPublicInfo(
                    GuidGenerator.Create(), id,
                    input.ElectricityUnitPrice, input.WaterUnitPrice,
                    input.ServiceFee, NormalizeText(input.OtherInfo));
                info.ReplaceHouseRules(RulesOf(input.HouseRules));
                await _infoRepository.InsertAsync(info, autoSave: true);
            }
            else
            {
                info.Update(
                    input.ElectricityUnitPrice, input.WaterUnitPrice,
                    input.ServiceFee, NormalizeText(input.OtherInfo));
                info.ReplaceHouseRules(RulesOf(input.HouseRules));
                await _infoRepository.UpdateAsync(info, autoSave: true);
            }

            return ToDto(info);
        });
    }

    private static void ValidateRules(List<HouseRuleItemDto>? rules)
    {
        if (rules is null || rules.Count == 0)
            return;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Text))
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidHouseRule);
            if (!Enum.IsDefined(rule.Icon))
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidHouseRule);
        }
    }

    private static IEnumerable<(HouseRuleIcon, string)> RulesOf(
        List<HouseRuleItemDto>? rules)
    {
        if (rules is null)
            return Array.Empty<(HouseRuleIcon, string)>();
        return rules
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => (x.Icon, x.Text));
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BuildingPublicInfoDto ToDto(BuildingPublicInfo info)
    {
        var dto = new BuildingPublicInfoDto
        {
            BuildingId = info.BuildingId,
            ElectricityUnitPrice = info.ElectricityUnitPrice,
            WaterUnitPrice = info.WaterUnitPrice,
            ServiceFee = info.ServiceFee,
            OtherInfo = info.OtherInfo
        };

        if (!string.IsNullOrWhiteSpace(info.HouseRulesJson))
        {
            dto.HouseRules = JsonSerializer
                .Deserialize<List<HouseRuleItemDto>>(info.HouseRulesJson)
                ?? new List<HouseRuleItemDto>();
        }

        return dto;
    }
}
