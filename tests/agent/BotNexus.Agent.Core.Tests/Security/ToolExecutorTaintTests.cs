using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Security;

/// <summary>
/// Covers the executor's contribution to #2519: every dispatched tool's declared content source is
/// folded into the ambient run taint, before any sibling in the same batch can execute.
/// </summary>
public sealed class ToolExecutorTaintTests
{
    [Fact]
    public async Task ExecuteAsync_LocalToolOnly_LeavesTurnClean()
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new SourceDeclaringTool("read", ToolContentSource.Local);

        await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "read"));

        scope.State.IsTainted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ToolContentSource.Network)]
    [InlineData(ToolContentSource.Untrusted)]
    public async Task ExecuteAsync_ForeignSourcedTool_TaintsTheTurn(string source)
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new SourceDeclaringTool("web_fetch", source);

        await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "web_fetch"));

        scope.State.IsTainted.ShouldBeTrue();
        scope.State.DescribeContributors().ShouldBe($"web_fetch ({source})");
    }

    /// <summary>
    /// Fail-closed: a tool that never declared a content source - the shape every pre-existing and
    /// every future third-party tool has until classified - must taint rather than pass as trusted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnclassifiedTool_TaintsTheTurn()
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new UnclassifiedTool("mystery");

        await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "mystery"));

        scope.State.IsTainted.ShouldBeTrue();
        scope.State.DescribeContributors().ShouldBe($"mystery ({ToolContentSource.Unknown})");
    }

    /// <summary>
    /// A tool whose declaration throws must not be credited as trusted, and must not take the turn
    /// down either.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ToolWhoseDeclarationThrows_TaintsTheTurn()
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new ThrowingSourceTool("hostile");

        await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "hostile"));

        scope.State.IsTainted.ShouldBeTrue();
        scope.State.DescribeContributors().ShouldBe($"hostile ({ToolContentSource.Unknown})");
    }

    /// <summary>
    /// A tool that resolved but then FAILED still taints. A failed fetch can surface a
    /// server-controlled error body, so failure is not evidence of cleanliness.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ForeignSourcedToolThatThrows_StillTaintsTheTurn()
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new FailingNetworkTool("web_fetch");

        var results = await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "web_fetch"));

        results.ShouldHaveSingleItem().IsError.ShouldBeTrue();
        scope.State.IsTainted.ShouldBeTrue();
    }

    /// <summary>
    /// An unregistered tool name resolves to nothing, executes nothing, and produces only the
    /// executor's own locally generated diagnostic. Tainting on it would let a model deny its own
    /// memory writes by calling a nonexistent tool.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnregisteredTool_DoesNotTaint()
    {
        using var scope = TurnTaintScope.Begin();
        var tool = new SourceDeclaringTool("read", ToolContentSource.Local);

        var results = await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "no_such_tool"));

        results.ShouldHaveSingleItem().IsError.ShouldBeTrue();
        scope.State.IsTainted.ShouldBeFalse();
    }

    /// <summary>
    /// The ordering guarantee. In parallel mode a fast local tool can finish long before a slow
    /// network one; the taint must already be recorded regardless, because a memory_save dispatched
    /// in the same batch would otherwise observe a clean run and launder the fetched content.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ParallelMode_TaintIsRecordedBeforeAnySiblingCompletes()
    {
        using var scope = TurnTaintScope.Begin();
        var slowNetwork = new SourceDeclaringTool("web_fetch", ToolContentSource.Network, delayMs: 150);
        var fastLocal = new TaintObservingTool("memory_save");

        await RunAsync(
            [slowNetwork, fastLocal],
            ToolExecutionMode.Parallel,
            ("t1", "web_fetch"),
            ("t2", "memory_save"));

        // Observed from INSIDE the fast tool's execution, not merely after the batch drained.
        fastLocal.ObservedTaint.ShouldBeTrue();
        scope.State.IsTainted.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoAmbientScope_DoesNotThrow()
    {
        TurnTaintScope.CurrentState.ShouldBeNull();
        var tool = new SourceDeclaringTool("web_fetch", ToolContentSource.Network);

        var results = await RunAsync([tool], ToolExecutionMode.Sequential, ("t1", "web_fetch"));

        results.ShouldHaveSingleItem().IsError.ShouldBeFalse();
    }

    private static async Task<IReadOnlyList<ToolResultAgentMessage>> RunAsync(
        IReadOnlyList<IAgentTool> tools,
        ToolExecutionMode mode,
        params (string Id, string Name)[] calls)
    {
        var context = new AgentContext(null, [], tools);
        var assistant = new AssistantAgentMessage(
            Content: string.Empty,
            ToolCalls: calls
                .Select(call => new ToolCallContent(call.Id, call.Name, new Dictionary<string, object?>()))
                .ToList(),
            FinishReason: StopReason.ToolUse);

        return await ToolExecutor.ExecuteAsync(
            context,
            assistant,
            TestHelpers.CreateTestConfig(toolExecutionMode: mode),
            _ => Task.CompletedTask,
            CancellationToken.None);
    }

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    private sealed class SourceDeclaringTool(string name, string source, int delayMs = 0) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string ContentSource => source;
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public async Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]);
        }
    }

    /// <summary>Declares no ContentSource at all — the interface default applies.</summary>
    private sealed class UnclassifiedTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }

    private sealed class ThrowingSourceTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string ContentSource => throw new InvalidOperationException("declaration exploded");
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }

    private sealed class FailingNetworkTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string ContentSource => ToolContentSource.Network;
        public Tool Definition => new(name, "test tool", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
            => throw new HttpRequestException("remote refused the connection");
    }

    /// <summary>Captures the ambient taint at the moment its own execution runs.</summary>
    private sealed class TaintObservingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string ContentSource => ToolContentSource.Local;
        public Tool Definition => new(name, "test tool", Schema);
        public bool ObservedTaint { get; private set; }

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            ObservedTaint = TurnTaintScope.IsCurrentTurnTainted;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }
}
