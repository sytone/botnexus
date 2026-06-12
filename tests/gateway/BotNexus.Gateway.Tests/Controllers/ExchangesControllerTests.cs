using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Controllers;

public sealed class ExchangesControllerTests
{
    private static AgentExchangeBudgetTracker CreateTracker()
    {
        var options = Options.Create(new AgentExchangeBudgetOptions());
        var logger = NullLogger<AgentExchangeBudgetTracker>.Instance;
        return new AgentExchangeBudgetTracker(options, logger);
    }

    [Fact]
    public void GetBudget_NullTracker_ReturnsEmptyResponse()
    {
        var controller = new ExchangesController(null);
        var result = controller.GetBudget() as OkObjectResult;

        result.ShouldNotBeNull();
        var json = JsonSerializer.Serialize(result.Value);
        json.ShouldContain("\"totalPairs\":0");
        json.ShouldContain("\"pairs\":[]");
    }

    [Fact]
    public void GetBudget_WithExchanges_ReturnsPairInfo()
    {
        var tracker = CreateTracker();
        tracker.RecordExchangeComplete(AgentId.From("alpha"), AgentId.From("beta"), 5);

        var controller = new ExchangesController(tracker);
        var result = controller.GetBudget() as OkObjectResult;

        result.ShouldNotBeNull();
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json.ShouldContain("\"initiator\":\"alpha\"");
        json.ShouldContain("\"target\":\"beta\"");
        json.ShouldContain("\"dailyTurnsUsed\":5");
        json.ShouldContain("\"totalPairs\":1");
    }

    [Fact]
    public void GetBudget_FilterByInitiator_FiltersCorrectly()
    {
        var tracker = CreateTracker();
        tracker.RecordExchangeComplete(AgentId.From("alpha"), AgentId.From("beta"), 3);
        tracker.RecordExchangeComplete(AgentId.From("gamma"), AgentId.From("beta"), 2);

        var controller = new ExchangesController(tracker);
        var result = controller.GetBudget(initiator: "alpha") as OkObjectResult;

        result.ShouldNotBeNull();
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json.ShouldContain("\"totalPairs\":1");
        json.ShouldContain("\"initiator\":\"alpha\"");
        json.ShouldNotContain("\"initiator\":\"gamma\"");
    }

    [Fact]
    public void GetBudget_FilterByTarget_FiltersCorrectly()
    {
        var tracker = CreateTracker();
        tracker.RecordExchangeComplete(AgentId.From("alpha"), AgentId.From("beta"), 3);
        tracker.RecordExchangeComplete(AgentId.From("alpha"), AgentId.From("delta"), 2);

        var controller = new ExchangesController(tracker);
        var result = controller.GetBudget(target: "delta") as OkObjectResult;

        result.ShouldNotBeNull();
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json.ShouldContain("\"totalPairs\":1");
        json.ShouldContain("\"target\":\"delta\"");
        json.ShouldNotContain("\"target\":\"beta\"");
    }

    [Fact]
    public void GetBudget_CooldownActive_ReportsCorrectly()
    {
        var tracker = CreateTracker();

        // Trigger cooldown by hitting loop threshold
        var options = new AgentExchangeBudgetOptions();
        for (int i = 0; i < options.LoopThreshold; i++)
        {
            try
            {
                tracker.EnsureWithinBudget(AgentId.From("looper"), AgentId.From("target"));
                tracker.RecordExchangeComplete(AgentId.From("looper"), AgentId.From("target"), 1);
            }
            catch (InvalidOperationException)
            {
                // Expected on final iteration when loop is detected
            }
        }

        var controller = new ExchangesController(tracker);
        var result = controller.GetBudget() as OkObjectResult;

        result.ShouldNotBeNull();
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        json.ShouldContain("cooldownRemainingSeconds");
    }
}
