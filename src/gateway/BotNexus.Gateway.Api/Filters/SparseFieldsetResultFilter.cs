using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Filters;

/// <summary>
/// Implements JSON:API-style sparse fieldsets for GET endpoints. When a request carries a
/// <c>?fields=a,b,c</c> query parameter, the serialized response body is projected down to only
/// the requested top-level fields. This works uniformly across every controller because it operates
/// on the serialized <see cref="JsonNode"/> rather than on strongly-typed DTOs, so no per-controller
/// or per-DTO code is required.
/// </summary>
/// <remarks>
/// Design contract (issue #1782):
/// <list type="bullet">
/// <item>Field names are comma-separated and matched case-insensitively against top-level JSON
/// property names.</item>
/// <item>When the parameter is absent or empty, the full object is returned unchanged - this keeps
/// the behaviour non-breaking for every existing consumer.</item>
/// <item>Applies to both collection (JSON array) and single-item (JSON object) GET responses.</item>
/// <item>Unknown field names are silently ignored (lenient); they never produce a 400.</item>
/// <item>Nested field selection is out of scope for v1 - only top-level properties are projected.</item>
/// </list>
/// Only successful <see cref="ObjectResult"/> payloads with a 2xx status are projected; error
/// bodies, <c>ProblemDetails</c>, and non-object/array payloads pass through untouched.
/// </remarks>
public sealed class SparseFieldsetResultFilter : IAsyncResultFilter
{
    /// <summary>The query-string parameter name that carries the requested field list.</summary>
    public const string QueryParameterName = "fields";

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (TryGetRequestedFields(context.HttpContext.Request, out var requestedFields)
            && context.Result is ObjectResult { Value: not null } objectResult
            && IsSuccessStatus(objectResult.StatusCode)
            && !IsErrorPayload(objectResult.Value))
        {
            var options = ResolveSerializerOptions(context.HttpContext);
            var projected = ProjectValue(objectResult.Value, requestedFields, options);
            if (projected is not null)
            {
                objectResult.Value = projected;
                objectResult.DeclaredType = typeof(JsonNode);
            }
        }

        await next();
    }

    /// <summary>
    /// Extracts and normalizes the requested field set from a GET request. Returns <c>false</c> when
    /// projection should not happen (non-GET verb, missing parameter, or no usable field names).
    /// </summary>
    private static bool TryGetRequestedFields(HttpRequest request, out HashSet<string> fields)
    {
        fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!HttpMethods.IsGet(request.Method))
            return false;

        if (!request.Query.TryGetValue(QueryParameterName, out var raw))
            return false;

        foreach (var chunk in raw)
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            foreach (var name in chunk.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                fields.Add(name);
        }

        return fields.Count > 0;
    }

    private static bool IsSuccessStatus(int? statusCode)
        => statusCode is null || (statusCode >= 200 && statusCode < 300);

    // ProblemDetails / ValidationProblemDetails carry error semantics and must never be projected.
    private static bool IsErrorPayload(object value)
        => value is ProblemDetails;

    private static JsonSerializerOptions? ResolveSerializerOptions(HttpContext httpContext)
        => httpContext.RequestServices?.GetService(typeof(IOptions<JsonOptions>)) is IOptions<JsonOptions> jsonOptions
            ? jsonOptions.Value.JsonSerializerOptions
            : null;

    /// <summary>
    /// Serializes the value to a <see cref="JsonNode"/> and projects it. Returns <c>null</c> when the
    /// payload is neither a JSON object nor an array of objects (nothing to project) or serialization
    /// fails, in which case the original value is left untouched.
    /// </summary>
    private static JsonNode? ProjectValue(object value, HashSet<string> fields, JsonSerializerOptions? options)
    {
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        switch (node)
        {
            case JsonObject obj:
                ProjectObject(obj, fields);
                return obj;
            case JsonArray array:
                foreach (var element in array)
                {
                    if (element is JsonObject elementObject)
                        ProjectObject(elementObject, fields);
                }

                return array;
            default:
                return null;
        }
    }

    /// <summary>Removes every top-level property that is not in the requested (case-insensitive) set.</summary>
    private static void ProjectObject(JsonObject obj, HashSet<string> fields)
    {
        var toRemove = obj
            .Select(static kvp => kvp.Key)
            .Where(key => !fields.Contains(key))
            .ToList();

        foreach (var key in toRemove)
            obj.Remove(key);
    }
}
