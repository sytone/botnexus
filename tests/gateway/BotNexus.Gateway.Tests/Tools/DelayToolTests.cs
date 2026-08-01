using System.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class DelayToolTests
{
    [Fact]
    public void DelayTool_HasCorrectNameAndLabel()
    {
        var tool = CreateDelayTool();

        tool.Name.ShouldBe("delay");
        tool.Label.ShouldBe("Delay / Wait");
    }

    /// <summary>
    /// The one deliberately real-time test: the default (production) construction must actually wait
    /// at least the requested duration. Lower bound only - <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// makes no upper-bound promise, so no upper bound is asserted here or anywhere in this file.
    /// </summary>
    [Fact]
    public async Task DelayTool_WaitsAtLeastRequestedDuration_UsingRealClock()
    {
        var tool = CreateRealDelayTool();
        var stopwatch = Stopwatch.StartNew();

        await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 1 });

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(900);
    }

    [Fact]
    public async Task DelayTool_RequestsExactRequestedDurationFromClock()
    {
        var recorder = new DelayRecorder();
        var tool = CreateDelayTool(delay: recorder.DelayAsync);

        await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 7 });

        recorder.Requested.ShouldBe([TimeSpan.FromSeconds(7)]);
    }

    [Fact]
    public async Task DelayTool_ClampsToMaxDelay()
    {
        var recorder = new DelayRecorder();
        var tool = CreateDelayTool(maxDelaySeconds: 2, delay: recorder.DelayAsync);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 9999 });

        recorder.Requested.ShouldBe([TimeSpan.FromSeconds(2)]);
        ReadText(result).ShouldContain("Waited 2 seconds");
    }

    [Fact]
    public async Task DelayTool_ClampsMinimumToOneSecond()
    {
        foreach (var seconds in new[] { 0, -5 })
        {
            var recorder = new DelayRecorder();
            var tool = CreateDelayTool(delay: recorder.DelayAsync);

            var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = seconds });

            recorder.Requested.ShouldBe([TimeSpan.FromSeconds(1)]);
            ReadText(result).ShouldContain("Waited 1 seconds");
        }
    }

    [Fact]
    public async Task DelayTool_ReturnsSuccessMessage()
    {
        var tool = CreateDelayTool(delay: NoOpDelay);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 1 });

        ReadText(result).ShouldContain("Waited 1 seconds");
    }

    [Fact]
    public async Task DelayTool_CancellationReturnsInfoNotError()
    {
        var tool = CreateDelayTool(delay: static (duration, token) => Task.Delay(duration, token));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?> { ["seconds"] = 10 },
            cts.Token);

        ReadText(result).ToLowerInvariant().ShouldContain("cancel");
    }

    [Fact]
    public async Task DelayTool_IncludesReasonInResult()
    {
        var tool = CreateDelayTool(delay: NoOpDelay);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["seconds"] = 1,
            ["reason"] = "waiting for build output"
        });

        ReadText(result).ShouldContain("waiting for build output");
    }

    [Fact]
    public async Task DelayTool_RespectsConfiguredMax()
    {
        var recorder = new DelayRecorder();
        var tool = CreateDelayTool(maxDelaySeconds: 3, delay: recorder.DelayAsync);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 30 });

        recorder.Requested.ShouldBe([TimeSpan.FromSeconds(3)]);
        ReadText(result).ShouldContain("Waited 3 seconds");
    }

    [Fact]
    public async Task DelayTool_RequiresSecondsParameter()
    {
        var tool = CreateDelayTool();

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    private sealed class DelayRecorder
    {
        private readonly List<TimeSpan> _requested = [];

        public IReadOnlyList<TimeSpan> Requested => _requested;

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requested.Add(duration);
            return Task.CompletedTask;
        }
    }

    private static Task NoOpDelay(TimeSpan duration, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-delay-test", prepared, cancellationToken);
    }

    private static IAgentTool CreateDelayTool(int? maxDelaySeconds = null, DelayAsync? delay = null)
        => new DelayTool(
            Options.Create(new DelayToolOptions { MaxDelaySeconds = maxDelaySeconds ?? 1800 }),
            delay ?? NoOpDelay);

    private static IAgentTool CreateRealDelayTool(int? maxDelaySeconds = null)
        => new DelayTool(Options.Create(new DelayToolOptions
        {
            MaxDelaySeconds = maxDelaySeconds ?? 1800
        }));

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
