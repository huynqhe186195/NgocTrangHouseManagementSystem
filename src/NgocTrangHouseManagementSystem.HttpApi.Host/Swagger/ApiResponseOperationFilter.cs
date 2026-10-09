using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using NgocTrangHouseManagementSystem.Responses;
using Swashbuckle.AspNetCore.SwaggerGen;
using System;
using System.Collections.Generic;

namespace NgocTrangHouseManagementSystem.Swagger;

public sealed class ApiResponseOperationFilter
    : IOperationFilter
{
    public void Apply(
        OpenApiOperation operation,
        OperationFilterContext context)
    {
        if (!ShouldWrap(
                context.ApiDescription.RelativePath))
        {
            return;
        }

        var httpMethod = context.ApiDescription.HttpMethod;


        if (string.IsNullOrWhiteSpace(httpMethod))
        {
            return;
        }

        var responses = operation.Responses;

        if (responses is null)
        {
            return;
        }

        var errorSchema =
            context.SchemaGenerator.GenerateSchema(
                typeof(ApiResponse<object?>),
                context.SchemaRepository
            );

        if (HttpMethods.IsDelete(httpMethod))
        {
            operation.Responses.Remove("204");

            operation.Responses["200"] =
                new OpenApiResponse
                {
                    Description = "OK",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] =
                            new OpenApiMediaType
                            {
                                Schema = errorSchema
                            }
                    }
                };
        }

        foreach (var responseType
                 in context.ApiDescription.SupportedResponseTypes)
        {
            if (responseType.StatusCode < 200 ||
                responseType.StatusCode >= 300)
            {
                continue;
            }

            if (responseType.Type is null ||
                responseType.Type == typeof(void))
            {
                continue;
            }

            var statusCode =
                responseType.StatusCode.ToString();

            if (operation.Responses is null ||
                !operation.Responses.TryGetValue(
                    statusCode,
                    out var response) ||
                response.Content is null)
            {
                continue;
            }

            var wrappedType =
                typeof(ApiResponse<>)
                    .MakeGenericType(
                        responseType.Type
                    );

            var wrappedSchema =
                context.SchemaGenerator.GenerateSchema(
                    wrappedType,
                    context.SchemaRepository
                );

            foreach (var mediaType
                     in response.Content.Values)
            {
                mediaType.Schema = wrappedSchema;
            }
        }

        AddOrReplaceErrorResponse(
            operation, "400", "Bad Request", errorSchema
        );

        AddOrReplaceErrorResponse(
            operation, "404", "Not Found", errorSchema
        );

        AddOrReplaceErrorResponse(
            operation, "409", "Conflict", errorSchema
        );
    }

    private static void AddOrReplaceErrorResponse(
        OpenApiOperation operation,
        string statusCode,
        string description,
        IOpenApiSchema schema)
    {
        operation.Responses[statusCode] =
            new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] =
                        new OpenApiMediaType
                        {
                            Schema = schema
                        }
                }
            };
    }

    private static bool ShouldWrap(
        string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var path =
            "/" + relativePath.TrimStart('/');

        return path.StartsWith(
            "/api/building-management",
            StringComparison.OrdinalIgnoreCase
        );
    }
}
