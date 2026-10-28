using System.Reflection;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Tools;
using Shouldly;
using Xunit;

namespace BotNexus.Gateway.Tests.Contracts;

public sealed class GatewayToolContractTests
{
    [Fact]
    public void MinimalTool_Defaults_AreFailClosedAndOptOut()
    {
        IGatewayTool tool = new ProbeTool();
        tool.Name.ShouldBe(tool.Definition.Name);
        tool.Label.ShouldBe("Probe");
        tool.DefaultTimeout.ShouldBeNull();
        tool.TimeoutArgument.ShouldBeNull();
        tool.ContentSource.ShouldBe(ToolContentSource.Unknown);
        ToolContentSource.IsTainting(tool.ContentSource).ShouldBeTrue();
        tool.GetPromptSnippet().ShouldBeNull();
        tool.GetPromptGuidelines().ShouldBeEmpty();
    }

    [Fact]
    public void Provenance_NormalizesKnownValues_AndFailsClosedForEverythingElse()
    {
        ToolContentSource.All.ShouldBe(new[] { "local", "network", "untrusted", "unknown" });
        foreach (var source in ToolContentSource.All)
        {
            ToolContentSource.Normalize(" " + source.ToUpperInvariant() + " ").ShouldBe(source);
            ToolContentSource.IsTainting(source).ShouldBe(source != ToolContentSource.Local);
        }
        foreach (var source in new string?[] { null, "", " ", "locel", "trusted", "local network" })
        {
            ToolContentSource.Normalize(source).ShouldBe(ToolContentSource.Unknown);
            ToolContentSource.IsTainting(source).ShouldBeTrue();
        }
    }

    [Fact]
    public void Timeout_UsesDeclaredUnits_AndPreservesAliasAcrossJson()
    {
        var seconds = new ToolTimeoutArgument("timeout", ToolTimeoutUnit.Seconds);
        var milliseconds = RoundTrip(new ToolTimeoutArgument("timeoutMs", ToolTimeoutUnit.Milliseconds, "timeout"));
        milliseconds.ArgumentName.ShouldBe("timeoutMs");
        milliseconds.DeprecatedAliasName.ShouldBe("timeout");
        milliseconds.Unit.ShouldBe(ToolTimeoutUnit.Milliseconds);
        foreach (var raw in new[] { Json("1500"), Json("\"1500\"") })
        {
            seconds.ToTimeSpan(raw).ShouldBe(TimeSpan.FromSeconds(1500));
            milliseconds.ToTimeSpan(raw).ShouldBe(TimeSpan.FromMilliseconds(1500));
        }
        foreach (var raw in new JsonElement?[] { null, Json("null"), Json("0"), Json("-1"), Json("1.5"), Json("2147483648"), Json("true"), Json("{}"), Json("\"bad\"") })
            milliseconds.ToTimeSpan(raw).ShouldBeNull();
    }

    [Fact]
    public void Definition_JsonRoundTrip_PreservesSchemaWithoutMutation()
    {
        var schema = Json("""{"type":"object","properties":{"value":{"type":["string","null"],"default":null}},"required":["value"],"additionalProperties":false,"x-vendor":{"flag":true}}""");
        var definition = RoundTrip(new GatewayToolDefinition("probe", "Description", schema));
        definition.Name.ShouldBe("probe");
        definition.Description.ShouldBe("Description");
        JsonElement.DeepEquals(definition.Parameters, schema).ShouldBeTrue();
        definition.Parameters.GetProperty("required")[0].GetString().ShouldBe("value");
        definition.Parameters.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Result_JsonRoundTrip_PreservesOrderedContentAndSeparateMetadata()
    {
        var details = Json("""{"nested":[null,true,9007199254740993,{"arbitrary":"value"}],"error":"tool-owned"}""");
        var delivery = Json("""{"outcome":"interrupted","isTruncated":true,"artifacts":[{"id":"a"}],"toolDeliveryDetails":{"attempt":2}}""");
        var result = RoundTrip(new GatewayToolResult([
            new(GatewayToolContentType.Text, "hello\nworld"),
            new(GatewayToolContentType.Image, "data:image/png;base64,AA==")], details)
        { DeliveryDetails = delivery, IsIncomplete = true });
        result.Content.Count.ShouldBe(2);
        result.Content[0].ShouldBe(new GatewayToolContent(GatewayToolContentType.Text, "hello\nworld"));
        result.Content[1].ShouldBe(new GatewayToolContent(GatewayToolContentType.Image, "data:image/png;base64,AA=="));
        result.IsIncomplete.ShouldBeTrue();
        JsonElement.DeepEquals(result.Details ?? throw new InvalidOperationException("Lost details"), details).ShouldBeTrue();
        JsonElement.DeepEquals(result.DeliveryDetails ?? throw new InvalidOperationException("Lost delivery details"), delivery).ShouldBeTrue();
        var empty = RoundTrip(new GatewayToolResult([]));
        empty.Content.ShouldBeEmpty();
        empty.Details.ShouldBeNull();
        empty.DeliveryDetails.ShouldBeNull();
        empty.IsIncomplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Invocation_PreservesArgumentsCancellationAndPartialUpdates()
    {
        IGatewayTool tool = new ProbeTool();
        var arguments = new Dictionary<string, JsonElement> { ["nested"] = Json("""{"array":[1,null,true]}""") };
        using var cancellation = new CancellationTokenSource();
        var prepared = await tool.PrepareArgumentsAsync(arguments, cancellation.Token);
        prepared.ShouldBeSameAs(arguments);
        var updates = new List<GatewayToolResult>();
        var result = await tool.ExecuteAsync("call-1", prepared, cancellation.Token, updates.Add);
        updates.ShouldHaveSingleItem().Content[0].Value.ShouldBe("partial");
        result.Content[0].Value.ShouldBe("call-1");
        JsonElement.DeepEquals(result.Details ?? throw new InvalidOperationException("Lost arguments"), arguments["nested"]).ShouldBeTrue();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.PrepareArgumentsAsync(arguments, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync("call-1", prepared, cancellation.Token));
    }

    [Fact]
    public async Task OptionalCapabilities_PreserveAdmittedContextAndLifecycle()
    {
        var probe = new ContextProbeTool();
        IContextAwareGatewayTool tool = probe;
        var call = RoundTrip(new GatewayToolCall("call-2", "probe", new Dictionary<string, JsonElement> { ["value"] = Json("42") }, "signature"));
        call.ThoughtSignature.ShouldBe("signature");
        var validated = new Dictionary<string, JsonElement>(call.Arguments) { ["runtimeHint"] = Json("true") };
        var context = new GatewayToolExecutionContext(
            Json("""{"role":"assistant","content":"","toolCalls":[],"usage":{"inputTokens":3},"contentBlocks":[{"type":"thinking","signature":"opaque"}]}"""),
            call, validated,
            new GatewayToolContext("system", [Json("""{"role":"user","content":"hello","images":[{"value":"image"}]}""")], [tool]))
        { AgentRunId = AgentRunId.From("run-1") };
        var updates = new List<GatewayToolResult>();
        var result = await tool.ExecuteAsync(context, onUpdate: updates.Add);
        result.Content[0].Value.ShouldBe("run-1");
        probe.Context.ShouldBeSameAs(context);
        context.AgentContext.Tools[0].ShouldBeSameAs(tool);
        context.AgentContext.SystemPrompt.ShouldBe("system");
        context.ValidatedArgs["runtimeHint"].GetBoolean().ShouldBeTrue();
        context.ToolCallRequest.Arguments.ContainsKey("runtimeHint").ShouldBeFalse();
        context.ToolCallRequest.Arguments["value"].GetInt32().ShouldBe(42);
        tool.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        tool.ContentSource.ShouldBe(ToolContentSource.Local);
        tool.GetPromptSnippet().ShouldBe("probe guidance");
        tool.GetPromptGuidelines().ShouldBe(new[] { "first", "second" });
        context.AgentContext.Messages[0].GetProperty("images")[0].GetProperty("value").GetString().ShouldBe("image");
        context.AssistantMessage.GetProperty("contentBlocks")[0].GetProperty("signature").GetString().ShouldBe("opaque");
        updates.ShouldHaveSingleItem();
        ((IContextReplacementAwareGatewayTool)probe).OnContextReplaced();
        probe.Context.ShouldBeNull();
        ((IDurableInteractiveWaitGatewayTool)probe).TimeoutArgument.ShouldNotBeNull();
        await ((IAsyncDisposable)probe).DisposeAsync();
        probe.Disposed.ShouldBeTrue();
        var legacy = context with { AgentRunId = null };
        (await tool.ExecuteAsync(legacy)).Content[0].Value.ShouldBe("legacy");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(context, cancellation.Token));
    }

    [Fact]
    public void PublicSignatureGraph_ContainsOnlyGatewayDomainAndBclTypes()
    {
        var roots = typeof(IGatewayTool).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IGatewayTool).Namespace).ToArray();
        roots.Length.ShouldBeGreaterThanOrEqualTo(12);
        var visited = new HashSet<Type>();
        foreach (var root in roots) Visit(root, visited);
        visited.ShouldContain(typeof(GatewayToolResult));
        visited.ShouldContain(typeof(GatewayToolExecutionContext));
        visited.ShouldContain(typeof(GatewayToolUpdateCallback));
        // Negative probes prove traversal reaches nested generic payloads and callback parameters.
        Should.Throw<ShouldAssertException>(() => Visit(typeof(IReadOnlyList<ForbiddenPayload>), []));
        Should.Throw<ShouldAssertException>(() => Visit(typeof(Action<ForbiddenPayload>), []));
        Should.Throw<ShouldAssertException>(() => Visit(typeof(IReadOnlyDictionary<string, BotNexus.Agent.Core.Types.AgentToolResult>), []));
        Should.Throw<ShouldAssertException>(() => Visit(typeof(Action<BotNexus.Agent.Providers.Core.Models.Tool>), []));
    }

    private static void Visit(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;
        if (type.HasElementType) Visit(type.GetElementType() ?? throw new InvalidOperationException(), visited);
        foreach (var argument in type.GetGenericArguments()) Visit(argument, visited);
        if (type.IsGenericParameter)
            foreach (var constraint in type.GetGenericParameterConstraints()) Visit(constraint, visited);
        if (type.IsGenericParameter) return;
        var assembly = type.Assembly.GetName().Name ?? "";
        var allowed = assembly == "BotNexus.Gateway.Contracts" || assembly == "BotNexus.Domain"
            || assembly == "System.Private.CoreLib" || assembly == "System.Text.Json" || assembly == "System.Runtime";
        allowed.ShouldBeTrue($"Forbidden public contract type: {type.FullName} ({assembly})");
        // BCL/domain are terminal leaves after checking generic arguments. Traverse all gateway signatures.
        if (assembly != "BotNexus.Gateway.Contracts" && !typeof(Delegate).IsAssignableFrom(type)) return;
        if (type.BaseType is { } baseType) Visit(baseType, visited);
        foreach (var contract in type.GetInterfaces()) Visit(contract, visited);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var member in type.GetMembers(flags))
        {
            switch (member)
            {
                case MethodInfo method:
                    Visit(method.ReturnType, visited);
                    foreach (var parameter in method.GetParameters()) Visit(parameter.ParameterType, visited);
                    foreach (var argument in method.GetGenericArguments()) Visit(argument, visited);
                    break;
                case ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters()) Visit(parameter.ParameterType, visited);
                    break;
                case PropertyInfo property:
                    Visit(property.PropertyType, visited);
                    foreach (var parameter in property.GetIndexParameters()) Visit(parameter.ParameterType, visited);
                    break;
                case FieldInfo field: Visit(field.FieldType, visited); break;
                case EventInfo @event: Visit(@event.EventHandlerType ?? throw new InvalidOperationException(), visited); break;
                case Type nested: Visit(nested, visited); break;
            }
        }
    }

    private sealed class ForbiddenPayload;
    private static JsonElement Json(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value)) ?? throw new InvalidOperationException("Round trip returned null");

    private class ProbeTool : IGatewayTool
    {
        public string Name => "probe";
        public string Label => "Probe";
        public GatewayToolDefinition Definition => new(Name, "Probe", Json("""{"type":"object"}"""));
        public Task<IReadOnlyDictionary<string, JsonElement>> PrepareArgumentsAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(arguments); }
        public Task<GatewayToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken = default, GatewayToolUpdateCallback? onUpdate = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onUpdate?.Invoke(new GatewayToolResult([new(GatewayToolContentType.Text, "partial")]));
            return Task.FromResult(new GatewayToolResult([new(GatewayToolContentType.Text, toolCallId)], arguments["nested"]));
        }
    }

    private sealed class ContextProbeTool : ProbeTool, IContextAwareGatewayTool, IContextReplacementAwareGatewayTool, IDurableInteractiveWaitGatewayTool, IAsyncDisposable
    {
        public GatewayToolExecutionContext? Context { get; private set; }
        public bool Disposed { get; private set; }
        public ToolTimeoutArgument? TimeoutArgument => new("timeoutSeconds", ToolTimeoutUnit.Seconds);
        public TimeSpan? DefaultTimeout => TimeSpan.FromSeconds(30);
        public string ContentSource => ToolContentSource.Local;
        public string? GetPromptSnippet() => "probe guidance";
        public IReadOnlyList<string> GetPromptGuidelines() => ["first", "second"];
        public Task<GatewayToolResult> ExecuteAsync(GatewayToolExecutionContext context, CancellationToken cancellationToken = default, GatewayToolUpdateCallback? onUpdate = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            var result = new GatewayToolResult([new(GatewayToolContentType.Text, context.AgentRunId?.Value ?? "legacy")]);
            onUpdate?.Invoke(result);
            return Task.FromResult(result);
        }
        public void OnContextReplaced() => Context = null;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
