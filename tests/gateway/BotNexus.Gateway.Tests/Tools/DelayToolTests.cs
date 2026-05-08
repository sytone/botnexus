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

    [Fact]
    public async Task DelayTool_WaitsSpecifiedDuration()
    {
        var tool = CreateDelayTool();
        var stopwatch = Stopwatch.StartNew();

        await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 2 });

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(1900);
    }

    [Fact]
    public async Task DelayTool_ClampsToMaxDelay()
    {
        var tool = CreateDelayTool(maxDelaySeconds: 2);
        var stopwatch = Stopwatch.StartNew();

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 9999 });

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(1900);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(4000);
        ReadText(result).ShouldContain("Waited 2 seconds");
    }

    [Fact]
    public async Task DelayTool_ClampsMinimumToOneSecond()
    {
        var tool = CreateDelayTool();

        foreach (var seconds in new[] { 0, -5 })
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = seconds });

            stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(900);
            stopwatch.ElapsedMilliseconds.ShouldBeLessThan(3000);
            ReadText(result).ShouldContain("Waited 1 seconds");
        }
    }

    [Fact]
    public async Task DelayTool_ReturnsSuccessMessage()
    {
        var tool = CreateDelayTool();

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 1 });

        ReadText(result).ShouldContain("Waited 1 seconds");
    }

    [Fact]
    public async Task DelayTool_CancellationReturnsInfoNotError()
    {
        var tool = CreateDelayTool();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?> { ["seconds"] = 10 },
            cts.Token);

        ReadText(result).ToLowerInvariant().ShouldContain("cancel");
    }

    [Fact]
    public async Task DelayTool_IncludesReasonInResult()
    {
        var tool = CreateDelayTool();

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
        var tool = CreateDelayTool(maxDelaySeconds: 3);
        var stopwatch = Stopwatch.StartNew();

        var result = await ExecuteAsync(tool, new Dictionary<string, object?> { ["seconds"] = 30 });

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(2900);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(5000);
        ReadText(result).ShouldContain("Waited 3 seconds");
    }

    [Fact]
    public async Task DelayTool_RequiresSecondsParameter()
    {
        var tool = CreateDelayTool();

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-delay-test", prepared, cancellationToken);
    }

    private static IAgentTool CreateDelayTool(int? maxDelaySeconds = null)
        => new DelayTool(Options.Create(new DelayToolOptions
        {
            MaxDelaySeconds = maxDelaySeconds ?? 1800
        }));

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
