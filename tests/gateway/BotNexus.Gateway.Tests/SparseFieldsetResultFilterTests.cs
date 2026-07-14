using System.Text.Json.Nodes;
using BotNexus.Gateway.Api.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SparseFieldsetResultFilter"/> - the reusable ?fields= projection
/// applied to GET responses (issue #1782). Tests drive the filter directly with a synthetic
/// <see cref="ResultExecutingContext"/> so they cover the projection contract without spinning up
/// the full MVC pipeline.
/// </summary>
public sealed class SparseFieldsetResultFilterTests
{
    private sealed record Sample(string Id, string DisplayName, string Description, int Rank);

    [Fact]
    public async Task SingleItem_WithFieldsSubset_ReturnsOnlyRequestedFields()
    {
        var payload = new Sample("agent-a", "Agent A", "long description", 7);
        var (context, next) = CreateContext("GET", payload, "id,displayName");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var obj = ExtractObject(context);
        obj.ContainsKey("Id").ShouldBeTrue();
        obj.ContainsKey("DisplayName").ShouldBeTrue();
        obj.ContainsKey("Description").ShouldBeFalse();
        obj.ContainsKey("Rank").ShouldBeFalse();
    }

    [Fact]
    public async Task SingleItem_FieldMatchingIsCaseInsensitive()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("GET", payload, "ID,DESCRIPTION");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var obj = ExtractObject(context);
        obj.ContainsKey("Id").ShouldBeTrue();
        obj.ContainsKey("Description").ShouldBeTrue();
        obj.ContainsKey("DisplayName").ShouldBeFalse();
    }

    [Fact]
    public async Task Collection_WithFieldsSubset_ProjectsEveryElement()
    {
        var payload = new[]
        {
            new Sample("a", "A", "da", 1),
            new Sample("b", "B", "db", 2),
        };
        var (context, next) = CreateContext("GET", payload, "id");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var array = ExtractArray(context);
        array.Count.ShouldBe(2);
        foreach (var element in array)
        {
            var obj = element!.AsObject();
            obj.ContainsKey("Id").ShouldBeTrue();
            obj.ContainsKey("DisplayName").ShouldBeFalse();
            obj.ContainsKey("Description").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task UnknownFieldNames_AreIgnored_NotAnError()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("GET", payload, "id,doesNotExist");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var obj = ExtractObject(context);
        obj.ContainsKey("Id").ShouldBeTrue();
        obj.ContainsKey("DoesNotExist").ShouldBeFalse();
        // Only the one known field survives; unknown names produce no property and no 400.
        obj.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NoFieldsParameter_ReturnsFullObjectUnchanged()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("GET", payload, fields: null);

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        // Value must be left as the original object (no JsonNode projection).
        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.Value.ShouldBeOfType<Sample>();
    }

    [Fact]
    public async Task EmptyFieldsParameter_ReturnsFullObjectUnchanged()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("GET", payload, fields: "   ");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.Value.ShouldBeOfType<Sample>();
    }

    [Fact]
    public async Task NonGetRequest_IsNotProjected()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("POST", payload, "id");

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.Value.ShouldBeOfType<Sample>();
    }

    [Fact]
    public async Task NonSuccessStatus_IsNotProjected()
    {
        var payload = new Sample("agent-a", "Agent A", "desc", 1);
        var (context, next) = CreateContext("GET", payload, "id", statusCode: 404);

        await new SparseFieldsetResultFilter().OnResultExecutionAsync(context, next);

        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.Value.ShouldBeOfType<Sample>();
    }

    private static JsonObject ExtractObject(ResultExecutingContext context)
        => context.Result.ShouldBeOfType<ObjectResult>().Value.ShouldBeOfType<JsonObject>();

    private static JsonArray ExtractArray(ResultExecutingContext context)
        => context.Result.ShouldBeOfType<ObjectResult>().Value.ShouldBeOfType<JsonArray>();

    private static (ResultExecutingContext Context, ResultExecutionDelegate Next) CreateContext(
        string method,
        object payload,
        string? fields,
        int? statusCode = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (fields is not null)
            httpContext.Request.QueryString = new QueryString($"?fields={Uri.EscapeDataString(fields)}");

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        var result = new ObjectResult(payload) { StatusCode = statusCode };
        var context = new ResultExecutingContext(
            actionContext,
            [],
            result,
            controller: new object());

        ResultExecutionDelegate next = () => Task.FromResult(
            new ResultExecutedContext(actionContext, [], context.Result, controller: new object()));

        return (context, next);
    }
}
