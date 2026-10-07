using System.Collections.Generic;

namespace NgocTrangHouseManagementSystem.Responses;

public sealed class ApiError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? Details { get; init; }

    public IReadOnlyList<ApiValidationError> ValidationErrors { get; init; }
        = [];
}
