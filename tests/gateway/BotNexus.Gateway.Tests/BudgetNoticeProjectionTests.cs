using System.Threading.Channels;
using System.Text.Json;
using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class BudgetNoticeProjectionTests
{
    [Fact]
    public async Task RunEnded_ProjectsAuthoritativeBudgetNumbersFromCore()
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var end = new AgentEndEvent([], null, DateTimeOffset.UtcNow,
            new RunCompletionResult(RunCompletionStatus.Parked, ["unfinished"], RunStopReason.ToolResultBudget,
                ToolResultBudget: new ToolResultBudgetEvidence(128, 130, 2)));
        await InProcessAgentHandle.WriteAgentEventAsync(end, "message-budget", channel.Writer,
            InProcessAgentHandle.MapAgentEvent, CancellationToken.None, () => false, NullLogger.Instance,
            AgentId.From("agent-budget"), SessionId.From("session-budget"), false);
        channel.Writer.TryComplete();
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in channel.Reader.ReadAllAsync())
            events.Add(evt);
        var completion = events.ShouldHaveSingleItem().Completion.ShouldNotBeNull();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(completion));
        document.RootElement.TryGetProperty("ToolResultBudget", out var budget).ShouldBeTrue();
        budget.GetProperty("Limit").GetInt32().ShouldBe(128);
        budget.GetProperty("ExecutedResults").GetInt64().ShouldBe(130);
        budget.GetProperty("Overshoot").GetInt64().ShouldBe(2);
    }
}
