using System.Text.Json;
using BotNexus.Agent.Core.ExtensionPoints.ToolResults;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using static BotNexus.Gateway.Tests.RunCorrelation4796Fixture;

namespace BotNexus.Gateway.Tests;

public sealed class AgentRunReview4796Tests
{
    [Theory]
    [InlineData(1, 128)]
    [InlineData(3, 129)]
    public async Task IncompleteResults_StillReachEveryPolicyDecisionAndAbsoluteFuse(int batch, int expected)
    {
        var provider = new ScriptedProvider((call, _) => call <= 130 ? Calls(call, batch) : TextResponse());
        var decisions = 0;
        var tool = new IncompleteTool();
        var (_, handle) = Create(provider, [tool], progress: (context, _) =>
        {
            context.ToolResult.Result.IsIncomplete.ShouldBeTrue();
            decisions++;
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.Progress);
        });
        await using var owned = handle;
        var response = await handle.PromptAsync("incomplete");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Parked");
        decisions.ShouldBe(expected);
        tool.Executions.ShouldBe(expected);
        var guard = Guards(response.Completion)[0];
        guard.GetProperty("TotalResults").GetInt32().ShouldBe(expected);
        guard.GetProperty("AbsoluteLimitReached").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task CustomGuardKind_ExportsOnlySafeCategoryAndOpaqueReference()
    {
        var provider = new ScriptedProvider((call, _) => call <= 8 ? Calls(call) : TextResponse());
        var (agent, handle) = Create(provider, [new ProbeTool()], progress: (_, _) =>
            Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress("private-scope", "private-evidence", "raw-secret-kind-4796")));
        AgentEndEvent? terminal = null;
        using var subscription = agent.Subscribe((evt, _) =>
        {
            if (evt is AgentEndEvent end) terminal = end;
            return Task.CompletedTask;
        });
        await using var owned = handle;
        var response = await handle.PromptAsync("custom");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Parked");
        var guards = Guards(response.Completion);
        guards[0].GetProperty("GuardKind").GetString().ShouldBe("classified-non-progress");
        guards.GetRawText().ShouldNotContain("raw-secret-kind-4796");
        guards.GetRawText().ShouldNotContain("private-scope");
        guards.GetRawText().ShouldNotContain("private-evidence");
        Guid.TryParseExact(guards[0].GetProperty("EvidenceReferences")[0].GetString(), "N", out _).ShouldBeTrue();
        var coreGuard = terminal.ShouldNotBeNull().Completion.ShouldNotBeNull().GuardObservations.ShouldHaveSingleItem();
        coreGuard.GuardKind.ShouldBe("classified-non-progress", "core evidence must sanitize before any gateway projection");
        JsonSerializer.Serialize(coreGuard).ShouldNotContain("raw-secret-kind-4796");
    }

    [Fact]
    public async Task FailedPolicyAfterWarning_PreservesGuardEvidenceAndPendingCall()
    {
        var provider = new ScriptedProvider((call, _) => Calls(call));
        var decisions = 0;
        var (_, handle) = Create(provider, [new ProbeTool()], progress: (_, _) =>
        {
            if (++decisions == 4) throw new InvalidOperationException("scripted policy failure");
            return Task.FromResult<ToolProgressDecision?>(ToolProgressDecision.NoProgress("scope", "evidence", "unchanged-read"));
        });
        await using var owned = handle;
        var response = await handle.PromptAsync("failure after warning");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Failed");
        response.ToolCalls.Count.ShouldBe(4);
        var guard = Guards(response.Completion)[0];
        guard.GetProperty("Disposition").GetString().ShouldBe("warning");
        guard.GetProperty("TotalResults").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task FailedToolStartListener_ReconstructsInterruptedCallFromRunSnapshot()
    {
        var provider = new ScriptedProvider((call, _) => Calls(call));
        var (agent, handle) = Create(provider, [new ProbeTool()]);
        using var subscription = agent.Subscribe((evt, _) => evt is ToolExecutionStartEvent
            ? throw new OperationCanceledException("unsignalled listener cancellation is a runtime failure")
            : Task.CompletedTask);
        await using var owned = handle;
        var response = await handle.PromptAsync("fail preparation");
        response.Completion.ShouldNotBeNull().Status.ShouldBe("Failed");
        var call = response.ToolCalls.ShouldHaveSingleItem();
        call.IsIncomplete.ShouldBeTrue();
        call.AgentRunId.ShouldBe(response.AgentRunId);
    }

    [Fact]
    public void MixedBlockingCapture_PreservesPerCallIdentityAndDoesNotInventFallback()
    {
        var first = BotNexus.Domain.Primitives.AgentRunId.From("first");
        var final = BotNexus.Domain.Primitives.AgentRunId.From("final");
        var response = new BotNexus.Gateway.Abstractions.Models.AgentResponse
        {
            Content = "merged", AgentRunId = final,
            ToolCalls = [
                new("same", "probe", false) { AgentRunId = first },
                new("same", "probe", false) { AgentRunId = final },
                new("legacy", "probe", false)]
        };
        var rows = BotNexus.Gateway.Audit.DefaultToolAuditSink.Instance.CaptureBlockingRun(response);
        rows.Select(row => row.AgentRunId).ShouldBe([first, final, null]);
        var safe = response with { ToolCalls = [new("single", "probe", false)] };
        BotNexus.Gateway.Audit.DefaultToolAuditSink.Instance.CaptureBlockingRun(safe).ShouldHaveSingleItem().AgentRunId.ShouldBe(final);
    }

    [Fact]
    public void FinalizationMerge_AttributesEachOriginalResponseBeforeCombining()
    {
        var first = BotNexus.Domain.Primitives.AgentRunId.From("exploration");
        var final = BotNexus.Domain.Primitives.AgentRunId.From("finalization");
        var exploration = new BotNexus.Gateway.Abstractions.Models.AgentResponse
        {
            Content = "exploration", AgentRunId = first, ToolCalls = [new("reused", "probe", false)]
        };
        var finalResponse = new BotNexus.Gateway.Abstractions.Models.AgentResponse
        {
            Content = "final", AgentRunId = final, ToolCalls = [new("reused", "probe", false)]
        };
        var merge = typeof(BotNexus.Gateway.Agents.DefaultSubAgentManager).GetMethod("MergeFinalizationOutcome",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).ShouldNotBeNull();
        var response = merge.Invoke(null, [exploration, finalResponse])
            .ShouldBeOfType<BotNexus.Gateway.Abstractions.Models.AgentResponse>();
        response.AgentRunId.ShouldBeNull("two admissions do not have a single authoritative run ID");
        response.ToolCalls.Select(call => call.AgentRunId).ShouldBe([first, final]);
        BotNexus.Gateway.Audit.DefaultToolAuditSink.Instance.CaptureBlockingRun(response)
            .Select(row => row.AgentRunId).ShouldBe([first, final]);
    }

    [Fact]
    public void GuardSanitizer_DropsRawAndPaddedReferencesAndBoundsOpaqueReferences()
    {
        var opaque = Guid.NewGuid().ToString("N");
        var guard = new BotNexus.Gateway.Abstractions.Models.GuardObservation("raw-secret-category", 6, 6, 3, 6, false,
            "raw-secret-disposition", ["/private/path", "raw-secret", " " + opaque + " ", opaque]);
        var sanitized = BotNexus.Gateway.Abstractions.Models.GuardEvidenceSanitizer.Sanitize([guard]).ShouldHaveSingleItem();
        sanitized.GuardKind.ShouldBe("classified-non-progress");
        sanitized.Disposition.ShouldBe("unknown");
        sanitized.EvidenceReferences.ShouldBe([opaque]);
        JsonSerializer.Serialize(sanitized).ShouldNotContain("raw-secret");
    }

    private sealed class IncompleteTool : IAgentTool
    {
        private int _executions;
        public int Executions => Volatile.Read(ref _executions);
        public string Name => "probe";
        public string Label => Name;
        public Tool Definition => new(Name, "scripted incomplete result", JsonSerializer.SerializeToElement(new { type = "object" }));
        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);
        public Task<AgentToolResult> ExecuteAsync(string toolCallId, IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default, AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "incomplete")]) { IsIncomplete = true });
        }
    }
}
