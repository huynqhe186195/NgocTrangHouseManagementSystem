using System;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Keeps the legacy Slice A delegation controller out of the default and
/// roster-only profiles. The legacy full-write lane opts in explicitly.
/// </summary>
public sealed class SliceARosterStagingControllerFeatureProvider
    : ControllerFeatureProvider
{
    private readonly bool _legacySliceAEnabled;

    public SliceARosterStagingControllerFeatureProvider(bool legacySliceAEnabled)
        => _legacySliceAEnabled = legacySliceAEnabled;

    protected override bool IsController(TypeInfo typeInfo)
    {
        if (!_legacySliceAEnabled &&
            typeInfo.AsType() == typeof(SliceAStagingOperatorGrantsController))
            return false;

        return base.IsController(typeInfo);
    }
}
