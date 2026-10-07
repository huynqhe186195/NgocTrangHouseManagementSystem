using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NgocTrangHouseManagementSystem.Responses;
using Volo.Abp.Http;

namespace NgocTrangHouseManagementSystem.Filters;

public sealed class ApiResponseFilter
    : IAsyncAlwaysRunResultFilter
{
    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        var traceId =
            Activity.Current?.Id
            ?? context.HttpContext.TraceIdentifier;

        var meta = new ApiResponseMeta
        {
            TraceId = traceId,
            Timestamp = DateTimeOffset.UtcNow
        };

        switch (context.Result)
        {
            case ObjectResult
            {
                Value: IApiResponse
            }:
                break;

            case ObjectResult
            {
                Value: RemoteServiceErrorResponse remoteError
            } objectResult:
            {
                objectResult.Value =
                    CreateErrorResponse(
                        remoteError,
                        objectResult.StatusCode,
                        meta
                    );

                break;
            }

            case ObjectResult objectResult:
            {
                var statusCode =
                    objectResult.StatusCode
                    ?? StatusCodes.Status200OK;

                if (statusCode < 400)
                {
                    objectResult.Value =
                        ApiResponse<object?>.Ok(
                            objectResult.Value,
                            meta
                        );
                }

                break;
            }

            case JsonResult jsonResult:
            {
                if (jsonResult.Value is not IApiResponse)
                {
                    jsonResult.Value =
                        ApiResponse<object?>.Ok(
                            jsonResult.Value,
                            meta
                        );
                }

                break;
            }
        }

        await next();
    }

    private static ApiResponse<object?> CreateErrorResponse(
        RemoteServiceErrorResponse remote,
        int? statusCode,
        ApiResponseMeta meta)
    {
        var validationErrors =
            remote.Error.ValidationErrors?
                .SelectMany(error =>
                {
                    if (error.Members is
                        { Length: > 0 })
                    {
                        return error.Members.Select(
                            member =>
                                new ApiValidationError
                                {
                                    Field = member,
                                    Message = error.Message
                                }
                        );
                    }

                    return
                    [
                        new ApiValidationError
                        {
                            Field = null,
                            Message = error.Message
                        }
                    ];
                })
                .ToList()
            ?? [];

        var code =
            remote.Error.Code
            ?? GetDefaultErrorCode(
                statusCode
                    ?? StatusCodes.Status500InternalServerError,
                validationErrors.Count > 0
            );

        var error = new ApiError
        {
            Code = code,
            Message =
                remote.Error.Message
                ?? "An error occurred.",
            Details = remote.Error.Details,
            ValidationErrors = validationErrors
        };

        return ApiResponse<object?>.Fail(
            error,
            meta
        );
    }

    private static string GetDefaultErrorCode(
        int statusCode,
        bool hasValidationErrors)
    {
        if (hasValidationErrors)
        {
            return "Common:ValidationError";
        }

        return statusCode switch
        {
            StatusCodes.Status400BadRequest =>
                "Common:BadRequest",

            StatusCodes.Status401Unauthorized =>
                "Common:Unauthorized",

            StatusCodes.Status403Forbidden =>
                "Common:Forbidden",

            StatusCodes.Status404NotFound =>
                "Common:NotFound",

            StatusCodes.Status409Conflict =>
                "Common:Conflict",

            _ =>
                "Common:InternalServerError"
        };
    }
}
