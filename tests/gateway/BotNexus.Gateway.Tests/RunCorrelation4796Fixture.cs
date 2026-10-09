using System.Reflection;
using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

// Reflection is only the bridge to additive contracts. Runs and projections use production APIs.
internal static class RunCorrelation4796Fixture
{
    internal static object Required(object owner, string name)
    {
        var property = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        property.ShouldNotBeNull($"#4796 requires {owner.GetType().Name}.{name}");
        return property.GetValue(owner).ShouldNotBeNull($"#4796 requires populated {name}");
    }

    internal static string Id(object owner)
    {
        var id = Required(owner, "AgentRunId");
        id.GetType().FullName.ShouldBe(owner is AgentEvent ? "BotNexus.Agent.Core.Types.AgentRunId" : "BotNexus.Domain.Primitives.AgentRunId",
            "Agent run identity must not reuse the cron RunId or a transport message id");
        var text = id.ToString().ShouldNotBeNull();
        text.ShouldNotBeNullOrWhiteSpace();
        return text;
    }

    internal static void SetId(object owner, string value)
    {
        var property = owner.GetType().GetProperty("AgentRunId").ShouldNotBeNull();
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        type.FullName.ShouldBe("BotNexus.Domain.Primitives.AgentRunId");
        var from = type.GetMethod("From", [typeof(string)]).ShouldNotBeNull();
        property.SetValue(owner, from.Invoke(null, [value]));
    }

    internal static JsonElement Guards(object completion)
    {
        var guards = Required(completion, "GuardObservations");
        return JsonSerializer.SerializeToElement(guards);
    }

    internal static void AssertGuard(object completion, string kind, int consecutive, int total, bool absolute)
    {
        var guards = Guards(completion);
        guards.GetArrayLength().ShouldBe(1);
        var guard = guards[0];
        guard.GetProperty("GuardKind").GetString().ShouldBe(kind);
        guard.GetProperty("ConsecutiveCount").GetInt32().ShouldBe(consecutive);
        guard.GetProperty("TotalResults").GetInt32().ShouldBe(total);
        guard.GetProperty("WarningThreshold").GetInt32().ShouldBe(3);
        guard.GetProperty("StopThreshold").GetInt32().ShouldBe(absolute ? 128 : 6);
        guard.GetProperty("AbsoluteLimitReached").GetBoolean().ShouldBe(absolute);
        // An allow-list is stronger than searching for one particular secret spelling.
        guard.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "AbsoluteLimitReached", "ConsecutiveCount", "GuardKind", "StopThreshold", "TotalResults", "WarningThreshold", "Disposition", "EvidenceReferences"
        }.Order());
        guards.GetRawText().ShouldNotContain("private-payload-4796");
    }

    internal static (BotNexus.Agent.Core.Agent Agent, InProcessAgentHandle Handle) Create(
        ScriptedProvider provider, IReadOnlyList<IAgentTool>? tools = null,
        RunCompletionPolicy? completion = null, ToolProgressPolicy? progress = null,
        BotNexus.Gateway.Audit.ToolAuditWriteAhead? audit = null, object? evidenceStore = null,
        Func<CancellationToken, Task<AgentContext?>>? compaction = null)
    {
        var model = new LlmModel("correlation-model", "Correlation model", provider.Api, "test-provider",
            "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 128_000, 1024);
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var options = new AgentOptions(
            InitialState: new AgentInitialState(SystemPrompt: "test", Model: model, Tools: tools ?? []),
            Model: model, LlmClient: new LlmClient(providers, models),
            ProviderMessageTransformer: null, AgentContextTransformer: null,
            ProviderExecutionOptionsProvider: (_, _) => Task.FromResult<ProviderExecutionOptions?>(null),
            SteeringMessageProvider: null, FollowUpMessageProvider: null,
            ToolExecutionMode: ToolExecutionMode.Parallel, ToolExecutionPolicy: null,
            ToolResultTransformer: audit is null ? null : (context, _) =>
            {
                audit.RecordCompleted(context.ToolCallRequest.Id);
                return Task.FromResult<ToolResultTransformResult?>(null);
            }, GenerationSettings: new GenerationOptions(),
            SteeringMode: QueueMode.All, FollowUpMode: QueueMode.All,
            RunCompletionPolicy: completion, ToolProgressPolicy: progress,
            ContextCompactionService: compaction,
            ToolAuditGate: audit is null ? null : async (context, ct) =>
            {
                await audit.PersistStartAsync(context.ToolCallRequest.Id, context.ToolCallRequest.Name,
                    context.ToolCallRequest.Arguments, ct);
                return null;
            });
        var agent = new BotNexus.Agent.Core.Agent(options);
        if (evidenceStore is null)
            return (agent, new InProcessAgentHandle(agent, AgentId.From("agent-4796"),
                SessionId.From("session-4796"), NullLogger.Instance, toolWriteAhead: audit));
        var capability = RunMeasurement4796Fixture.Capability(evidenceStore);
        var constructor = typeof(InProcessAgentHandle).GetConstructors().SingleOrDefault(c =>
            c.GetParameters().Any(p => p.ParameterType == capability))
            .ShouldNotBeNull("#4796 requires optional IAgentRunEvidenceStore injection on the real handle");
        var arguments = constructor.GetParameters().Select(p => p.Name switch
        {
            "agent" => (object)agent,
            "agentId" => AgentId.From("agent-4796"),
            "sessionId" => SessionId.From("session-4796"),
            "logger" => NullLogger.Instance,
            "tools" => tools,
            "toolWriteAhead" => audit,
            _ when p.ParameterType == capability => evidenceStore,
            _ => p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException($"Unexpected required handle argument {p.Name}")
        }).ToArray();
        return (agent, constructor.Invoke(arguments).ShouldBeOfType<InProcessAgentHandle>());
    }

    internal sealed class ScriptedProvider(Func<int, Context, LlmStream> script) : IApiProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Api => "run-correlation-4796";
        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context);
        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
            => script(Interlocked.Increment(ref _calls), context);
    }

    internal static LlmStream TextResponse(string text = "done") => Response([new TextContent(text)], StopReason.Stop);

    internal static LlmStream Calls(int turn, int count = 1, string name = "probe") => Response(
        Enumerable.Range(0, count).Select(index => (ContentBlock)new ToolCallContent($"call-{turn}-{index}", name,
            new Dictionary<string, object?> { ["payload"] = "private-payload-4796" })).ToArray(), StopReason.ToolUse);

    private static LlmStream Response(IReadOnlyList<ContentBlock> content, StopReason reason)
    {
        var message = new AssistantMessage(content, "run-correlation-4796", "test-provider", "correlation-model",
            Usage.Empty(), reason, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stream = new LlmStream();
        stream.Push(new StartEvent(message));
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is ToolCallContent call)
            {
                stream.Push(new ToolCallStartEvent(index, message));
                stream.Push(new ToolCallDeltaEvent(index, "{}", message));
                stream.Push(new ToolCallEndEvent(index, call, message));
            }
            else if (content[index] is TextContent text)
            {
                stream.Push(new TextStartEvent(index, message));
                stream.Push(new TextDeltaEvent(index, text.Text, message));
                stream.Push(new TextEndEvent(index, text.Text, message));
            }
        }
        stream.Push(new DoneEvent(reason, message));
        stream.End(message);
        return stream;
    }

    internal sealed class ProbeTool(Func<CancellationToken, Task<string>>? execute = null) : IAgentTool
    {
        private int _executions;
        public int Executions => Volatile.Read(ref _executions);
        public string Name => "probe";
        public string Label => Name;
        public Tool Definition => new(Name, "test probe", JsonSerializer.SerializeToElement(new { type = "object" }));
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);
        public async Task<AgentToolResult> ExecuteAsync(string toolCallId,
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executions);
            if (onUpdate is not null)
                onUpdate(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "progress")]));
            var text = execute is null ? "private-payload-4796" : await execute(cancellationToken);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]);
        }
    }
}
