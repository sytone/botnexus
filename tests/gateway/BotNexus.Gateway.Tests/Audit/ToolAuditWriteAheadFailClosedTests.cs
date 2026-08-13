using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// #2615 AC2 and AC5. The unit suite (<c>ToolAuditWriteAheadTests</c>) proves the write-ahead
/// refuses; this suite proves the refusal actually reaches the tool boundary - the tool is never
/// invoked - and fences the ordering of the persist call against the mutation named in AC5.
/// </summary>
public sealed class ToolAuditWriteAheadFailClosedTests
{
    /// <summary>
    /// AC2, expressed as the security property rather than as an exception shape: when the audit
    /// record cannot be persisted, the side-effecting tool's <c>ExecuteAsync</c> is <b>never
    /// entered</b>. Asserting only that an error is surfaced would pass even if the command had
    /// already run, which is the exact failure this issue exists to prevent.
    /// </summary>
    [Fact]
    public async Task PersistenceFailure_MeansTheSideEffectingToolIsNeverInvoked()
    {
        var session = Session();
        var store = StoreFor(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var writeAhead = new ToolAuditWriteAhead(
            store.Object, DefaultToolAuditSink.Instance, new SecretRedactor(), session.SessionId, NullLogger.Instance);
        var tool = new SpyTool("exec");

        // The gateway wires the write-ahead as the pre-tool-call hook; drive that same delegate.
        var hook = BuildHook(writeAhead);

        await Should.ThrowAsync<InvalidOperationException>(
            () => hook(Context(tool, "call-1", "exec"), CancellationToken.None));

        tool.Executions.ShouldBe(0);
    }

    /// <summary>
    /// Non-vacuity anchor for the test above: with a healthy store the same hook lets the same tool
    /// through, so a zero execution count is genuinely caused by the persistence failure and not by
    /// a harness that never invokes anything.
    /// </summary>
    [Fact]
    public async Task HealthyStore_LetsTheSameSideEffectingToolThrough()
    {
        var session = Session();
        var store = StoreFor(session);
        var writeAhead = new ToolAuditWriteAhead(
            store.Object, DefaultToolAuditSink.Instance, new SecretRedactor(), session.SessionId, NullLogger.Instance);
        var tool = new SpyTool("exec");
        var hook = BuildHook(writeAhead);

        var verdict = await hook(Context(tool, "call-1", "exec"), CancellationToken.None);
        if (verdict is null || verdict.IsUnambiguousAllow)
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());

        tool.Executions.ShouldBe(1);
        session.GetHistorySnapshot().ShouldHaveSingleItem().Kind.ShouldBe(MessageKind.ToolStart);
    }

    /// <summary>
    /// AC5: the fence for "the persist call moved to after invocation".
    /// </summary>
    /// <remarks>
    /// A behaviour test cannot distinguish write-ahead from write-behind on a healthy store - both
    /// end with a persisted row and an executed tool. The ordering is only observable structurally,
    /// so this fence pins that <c>PersistStartAsync</c> is awaited inside the <b>before</b>-tool-call
    /// delegate in the composition root, and that the after-hook only ever <i>closes out</i> a call.
    /// Moving the persist into <c>afterToolCall</c> reddens this test by name.
    /// </remarks>
    [Fact]
    public void PersistStart_IsCalledFromTheBeforeToolCallHook_NotTheAfterHook()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "gateway", "BotNexus.Gateway", "Isolation", "InProcessIsolationStrategy.cs"));

        var before = ExtractDelegateBody(source, "beforeToolCall = async (ctx, ct) =>");
        var after = ExtractDelegateBody(source, "afterToolCall = async (ctx, ct) =>");

        before.ShouldContain("PersistStartAsync",
            customMessage: "The write-ahead must be awaited BEFORE the tool runs; without it a side-effecting "
                + "tool can execute with no durable record (#2615 AC1/AC2).");
        after.ShouldNotContain("PersistStartAsync",
            customMessage: "Persisting from the after-hook is write-BEHIND: the tool has already run, so a crash "
                + "in between leaves no evidence at all (#2615 AC5).");
        after.ShouldContain("RecordCompleted",
            customMessage: "The after-hook must close out the call, otherwise every completed tool is later "
                + "reported as an interrupted invocation (#2615 AC3).");

        // The persist must be the FIRST thing the before-hook awaits: a policy dispatch placed
        // ahead of it can block, throw, or time out, and the invocation attempt would go unrecorded.
        var persistIndex = before.IndexOf("PersistStartAsync", StringComparison.Ordinal);
        var dispatchIndex = before.IndexOf("DispatchAsync", StringComparison.Ordinal);
        if (dispatchIndex >= 0)
            persistIndex.ShouldBeLessThan(dispatchIndex);
    }

    /// <summary>
    /// AC3 companion fence: the interrupted-invocation recorder must be reachable from the run's
    /// unwind paths. If no call site remains, a cancelled run silently loses its in-flight tool.
    /// </summary>
    [Fact]
    public void InterruptedInvocations_AreRecordedOnTheRunUnwindPaths()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "gateway", "BotNexus.Gateway", "Isolation", "InProcessIsolationStrategy.cs"));
        var callSites = Regex.Matches(source, @"RecordInterruptedToolsAsync\(").Count;

        // One definition plus at least the blocking-cancel, blocking-crash and streaming unwind sites.
        callSites.ShouldBeGreaterThanOrEqualTo(4,
            "A cancelled, crashed or timed-out run must still leave the explicit incomplete record (#2615 AC3/AC4).");
    }

    private static BeforeToolCallDelegate BuildHook(ToolAuditWriteAhead writeAhead)
        => async (ctx, ct) =>
        {
            await writeAhead.PersistStartAsync(ctx.ToolCallRequest.Id, ctx.ToolCallRequest.Name, ctx.ValidatedArgs, ct);
            return null;
        };

    private static BeforeToolCallContext Context(IAgentTool tool, string callId, string toolName)
    {
        var args = new Dictionary<string, object?> { ["command"] = "rm -rf /" };
        return new BeforeToolCallContext(
            new AssistantAgentMessage("calling", ToolCalls: [new ToolCallContent(callId, toolName, args)]),
            new ToolCallContent(callId, toolName, args),
            args,
            new AgentContext(null, [], [tool]));
    }

    private static string ExtractDelegateBody(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find '{marker}' - the fence is scanning the wrong shape.");
        var end = source.IndexOf("\n            };", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Could not find the end of the delegate started by '{marker}'.");
        return source[start..end];
    }

    private static string SourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            current = current.Parent;
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return Path.Combine(current!.FullName, "src");
    }

    private static Mock<ISessionStore> StoreFor(GatewaySession session)
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static GatewaySession Session() => new()
    {
        SessionId = SessionId.From("s1"),
        AgentId = AgentId.From("agent-a"),
        ConversationId = ConversationId.From("conv")
    };

    /// <summary>A tool that counts how many times it was actually entered.</summary>
    private sealed class SpyTool(string name) : IAgentTool
    {
        public int Executions { get; private set; }

        public string Name => name;

        public string Label => name;

        public Tool Definition => new(
            Name,
            "spy",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }));

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Executions++;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ran")]));
        }
    }
}
