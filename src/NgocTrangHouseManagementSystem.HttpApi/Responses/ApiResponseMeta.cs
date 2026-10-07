using System;

namespace NgocTrangHouseManagementSystem.Responses;

public sealed class ApiResponseMeta
{
    public required string TraceId { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
