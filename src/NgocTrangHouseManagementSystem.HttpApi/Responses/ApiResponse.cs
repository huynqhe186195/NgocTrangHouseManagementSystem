namespace NgocTrangHouseManagementSystem.Responses;

public interface IApiResponse
{
}

public sealed class ApiResponse<T> : IApiResponse
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public ApiError? Error { get; init; }

    public required ApiResponseMeta Meta { get; init; }

    public static ApiResponse<T> Ok(
        T? data,
        ApiResponseMeta meta)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Error = null,
            Meta = meta
        };
    }

    public static ApiResponse<T> Fail(
        ApiError error,
        ApiResponseMeta meta)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Data = default,
            Error = error,
            Meta = meta
        };
    }
}
