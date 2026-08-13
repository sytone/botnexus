using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Pins the unit semantics of the per-tool cancellation budget (issue #2955).
/// </summary>
/// <remarks>
/// The executor used to infer a unit from the argument NAME: any argument called <c>timeout</c> was
/// read as seconds. <c>ProcessTool.timeout</c> is milliseconds, so <c>timeout: 5000</c> widened the
/// budget to 5000 seconds instead of 5 seconds - a 1000x inflation that silently disabled the
/// safety cap. The unit is now declared by the tool.
/// </remarks>
public sealed class ToolExecutorTimeoutUnitTests
{
    private static readonly TimeSpan SafetyCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clause 2: a millisecond-declaring tool asking for 5000 must get a 5-second budget.
    /// </summary>
    [Fact]
    public void MillisecondDeclaringTool_Timeout5000_YieldsFiveSecondBudget_NotFiveThousand()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument(
            "timeoutMs", ToolTimeoutUnit.Milliseconds, DeprecatedAliasName: "timeout"));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeout"] = 5000 },
            SafetyCap);

        // 5000ms is BELOW the 30s safety cap, so the cap stands and the budget is never widened.
        resolved.ShouldBe(SafetyCap);
        resolved!.Value.ShouldBeLessThan(TimeSpan.FromSeconds(5000));
    }

    /// <summary>
    /// The same call under the old name-based rule would have produced 5000 seconds. Pin the
    /// arithmetic directly so the regression is unmistakable if inference ever returns.
    /// </summary>
    [Fact]
    public void MillisecondDeclaringTool_LargeRequest_WidensByMilliseconds_NotSeconds()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument(
            "timeoutMs", ToolTimeoutUnit.Milliseconds, DeprecatedAliasName: "timeout"));

        // 120_000ms = 2 minutes, comfortably above the 30s cap, so the budget IS widened.
        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeoutMs"] = 120_000 },
            SafetyCap);

        resolved.ShouldBe(TimeSpan.FromMilliseconds(120_000) + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Happy path for the seconds tools: ShellTool-style <c>timeout: 600</c> still widens in seconds.
    /// </summary>
    [Fact]
    public void SecondsDeclaringTool_Timeout600_WidensBySeconds()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument("timeout", ToolTimeoutUnit.Seconds));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeout"] = 600 },
            SafetyCap);

        resolved.ShouldBe(TimeSpan.FromSeconds(610));
    }

    /// <summary>
    /// Clause 3 (sad path): a tool that declares NO timeout argument must have none of its
    /// arguments inspected, even one spelled exactly <c>timeout</c>.
    /// </summary>
    [Fact]
    public void ToolDeclaringNoTimeoutArgument_BareTimeoutArgument_IsIgnored()
    {
        var tool = new DeclaringTool(timeoutArgument: null);

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeout"] = 5000 },
            SafetyCap);

        resolved.ShouldBe(SafetyCap);
    }

    /// <summary>
    /// The canonical argument wins over the deprecated alias when both are supplied.
    /// </summary>
    [Fact]
    public void CanonicalArgument_TakesPrecedenceOver_DeprecatedAlias()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument(
            "timeoutMs", ToolTimeoutUnit.Milliseconds, DeprecatedAliasName: "timeout"));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeoutMs"] = 120_000, ["timeout"] = 900_000 },
            SafetyCap);

        resolved.ShouldBe(TimeSpan.FromMilliseconds(120_000) + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Sad paths: junk, absent, zero and negative values leave the safety cap untouched.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnusableRequestedValue_LeavesSafetyCapIntact(object? rawValue)
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument("timeoutMs", ToolTimeoutUnit.Milliseconds));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeoutMs"] = rawValue },
            SafetyCap);

        resolved.ShouldBe(SafetyCap);
    }

    /// <summary>
    /// A requested timeout shorter than the safety cap never NARROWS the budget - the cap is a
    /// ceiling on runaway values, not a floor the agent can lower.
    /// </summary>
    [Fact]
    public void RequestedTimeoutBelowSafetyCap_DoesNotNarrowBudget()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument("timeout", ToolTimeoutUnit.Seconds));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeout"] = 1 },
            SafetyCap);

        resolved.ShouldBe(SafetyCap);
    }

    /// <summary>
    /// The tool-declared <c>DefaultTimeout</c> still raises the floor above the configured cap.
    /// </summary>
    [Fact]
    public void ToolDefaultTimeout_RaisesBudgetAboveSafetyCap()
    {
        var tool = new DeclaringTool(timeoutArgument: null, defaultTimeout: TimeSpan.FromMinutes(10));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(tool, new Dictionary<string, object?>(), SafetyCap);

        resolved.ShouldBe(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// With no configured safety cap and no tool default there is no budget to widen.
    /// </summary>
    [Fact]
    public void NoSafetyCapAndNoToolDefault_YieldsNoBudget()
    {
        var tool = new DeclaringTool(new ToolTimeoutArgument("timeout", ToolTimeoutUnit.Seconds));

        var resolved = ToolExecutor.ResolveEffectiveTimeout(
            tool,
            new Dictionary<string, object?> { ["timeout"] = 600 },
            toolTimeout: null);

        resolved.ShouldBeNull();
    }

    /// <summary>
    /// Unit conversion is a property of the declaration itself.
    /// </summary>
    [Fact]
    public void ToTimeSpan_UsesDeclaredUnit()
    {
        new ToolTimeoutArgument("t", ToolTimeoutUnit.Seconds)
            .ToTimeSpan(5000).ShouldBe(TimeSpan.FromSeconds(5000));
        new ToolTimeoutArgument("t", ToolTimeoutUnit.Milliseconds)
            .ToTimeSpan(5000).ShouldBe(TimeSpan.FromSeconds(5));
    }

    private sealed class DeclaringTool(
        ToolTimeoutArgument? timeoutArgument,
        TimeSpan? defaultTimeout = null) : IAgentTool
    {
        public string Name => "declaring";
        public string Label => "Declaring";
        public Tool Definition => new(Name, "test tool",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());
        public TimeSpan? DefaultTimeout => defaultTimeout;
        public ToolTimeoutArgument? TimeoutArgument => timeoutArgument;

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }
}
