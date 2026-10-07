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
        ResultExecutionDelegate next) // context = the response currently being prepared for dispatch
                                      // next = allows the response to proceed outward
    {

        if (ShouldSkipWrapping(context.HttpContext))
        {
            await next();
            return;
        }

        var traceId =
            Activity.Current?.Id
            ?? context.HttpContext.TraceIdentifier;

        var meta = new ApiResponseMeta
        {
            TraceId = traceId,
            Timestamp = DateTimeOffset.UtcNow
        }; // Get the identifier of the current request.

        if (context.Result is ObjectResult
            {
                Value: RemoteServiceErrorResponse remoteError
            } errorResult)
        {
            errorResult.Value =
                CreateErrorResponse(
                    remoteError,
                    errorResult.StatusCode,
                    meta
                );

            await next();
            return;
        }

        // DELETE thành công->luôn trả 200 + envelope
        if (HttpMethods.IsDelete(
                context.HttpContext.Request.Method)
            && IsEmptySuccessResult(context.Result))
        {
            context.HttpContext.Response.StatusCode =
                StatusCodes.Status200OK;

            context.Result = new ObjectResult(
                ApiResponse<object?>.Ok(
                    null,
                    meta
                )
            )
            {
                StatusCode = StatusCodes.Status200OK
            };

            await next();
            return;
        }

        switch (context.Result) // What type is the current response?
        {
            case ObjectResult
            {
                Value: IApiResponse // If the data has already been packaged according to our standard, do not package it again
            }:
                break;

            case ObjectResult objectResult:
                {
                    var statusCode =
                        objectResult.StatusCode
                        ?? StatusCodes.Status200OK;

                    if (statusCode < 400)
                    {
                        if (objectResult.Value is not IApiResponse)
                        {
                            objectResult.Value =
                                ApiResponse<object?>.Ok(
                                    objectResult.Value,
                                    meta
                                );
                        }

                        if (HttpMethods.IsPost(
                            context.HttpContext.Request.Method))
                        {
                            objectResult.StatusCode =
                                StatusCodes.Status201Created;

                            context.HttpContext.Response.StatusCode =
                                StatusCodes.Status201Created;
                        }
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

                await next(); // I've finished modifying the response. Now, let the pipeline proceed with sending the response to the client.
        }
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
            ?? []; // Get all validation error of ABP and then switch to ApiValidationError of system

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

    private static bool IsEmptySuccessResult(
    IActionResult result)
    {
        return result switch
        {
            EmptyResult => true,

            NoContentResult => true,

            StatusCodeResult statusCodeResult
                when statusCodeResult.StatusCode ==
                     StatusCodes.Status204NoContent
                => true,

            ObjectResult
            {
                Value: null
            } objectResult
                when objectResult.StatusCode is null
                     or StatusCodes.Status200OK
                     or StatusCodes.Status204NoContent
                => true,

            _ => false
        };
    }

    private static bool ShouldSkipWrapping(
    HttpContext httpContext)
    {
        var path = httpContext.Request.Path;

        return
            path.StartsWithSegments("/swagger") ||
            path.StartsWithSegments("/health-status") ||
            path.StartsWithSegments("/api/abp") ||
            path.StartsWithSegments("/connect") ||
            path.StartsWithSegments("/.well-known");
    }
}
