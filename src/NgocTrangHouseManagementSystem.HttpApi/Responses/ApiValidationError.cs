namespace NgocTrangHouseManagementSystem.Responses;

public sealed class ApiValidationError
{
    public string? Field { get; init; }

    public required string Message { get; init; }
}
