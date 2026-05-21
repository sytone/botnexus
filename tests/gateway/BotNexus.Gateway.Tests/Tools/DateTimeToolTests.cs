using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class DateTimeToolTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 5, 15, 23, 57, 11, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_WithoutTimezone_UsesConfiguredTimezone()
    {
        var tool = CreateTool("America/Los_Angeles");

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>());

        json.GetProperty("utc").GetString().ShouldBe("2026-05-15T23:57:11.0000000+00:00");
        json.GetProperty("local_date").GetString().ShouldBe("2026-05-15");
        json.GetProperty("local_time").GetString().ShouldBe("16:57:11");
        json.GetProperty("timezone").GetString().ShouldBe("America/Los_Angeles");
        json.GetProperty("day_of_week").GetString().ShouldBe("Friday");
        json.GetProperty("week_number").GetInt32().ShouldBe(20);
        json.GetProperty("unix_timestamp").GetInt64().ShouldBe(1778889431);
        json.GetProperty("offset").GetString().ShouldBe("-07:00:00");
        json.GetProperty("next_monday").GetString().ShouldBe("2026-05-18");
        json.GetProperty("this_monday").GetString().ShouldBe("2026-05-11");
    }

    [Fact]
    public async Task ExecuteAsync_WithTimezone_OverridesConfiguredTimezone()
    {
        var tool = CreateTool("America/Los_Angeles");

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>
        {
            ["timezone"] = "UTC"
        });

        json.GetProperty("local_date").GetString().ShouldBe("2026-05-15");
        json.GetProperty("local_time").GetString().ShouldBe("23:57:11");
        json.GetProperty("timezone").GetString().ShouldBe("UTC");
        json.GetProperty("offset").GetString().ShouldBe("00:00:00");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidTimezone_FallsBackToUtc()
    {
        var tool = CreateTool("Invalid/Timezone");

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>());

        json.GetProperty("timezone").GetString().ShouldBe("UTC");
        json.GetProperty("local_time").GetString().ShouldBe("23:57:11");
        json.GetProperty("offset").GetString().ShouldBe("00:00:00");
    }


    [Fact]
    public async Task ExecuteAsync_WithServerTimezone_FallsBackToServerDefault_WhenNoAgentTimezone()
    {
        // Simulates: server has DefaultTimezone configured, agent has no Soul.Timezone
        var tool = CreateTool(serverTimezone: "Europe/London");

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>());

        json.GetProperty("timezone").GetString().ShouldBe("Europe/London");
    }

    [Fact]
    public async Task ExecuteAsync_AgentTimezoneOverridesServerDefault()
    {
        // Agent Soul.Timezone takes precedence over server DefaultTimezone
        var tool = CreateTool(agentTimezone: "America/New_York", serverTimezone: "Europe/London");

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>());

        json.GetProperty("timezone").GetString().ShouldBe("America/New_York");
    }

    [Fact]
    public async Task ExecuteAsync_NoAgentOrServerTimezone_FallsBackToUtc()
    {
        var tool = CreateTool();

        var json = await ExecuteJsonAsync(tool, new Dictionary<string, object?>());

        json.GetProperty("timezone").GetString().ShouldBe("UTC");
    }

    private static DateTimeTool CreateTool(string? agentTimezone = null, string? serverTimezone = null)
    {
        var effectiveTimezone = agentTimezone ?? serverTimezone;
        return new(effectiveTimezone, () => FixedUtcNow);
    }



    private static async Task<JsonElement> ExecuteJsonAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        var result = await tool.ExecuteAsync("call-datetime-test", prepared, cancellationToken);
        var text = result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
