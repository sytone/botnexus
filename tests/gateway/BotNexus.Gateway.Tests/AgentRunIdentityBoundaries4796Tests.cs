using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using static BotNexus.Gateway.Tests.RunCorrelation4796Fixture;
using CoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Tests;

// Additional existing-runtime coverage; these are not claimed as pre-production RED tests.
public sealed class AgentRunIdentityBoundaries4796Tests
{
    [Fact]
    public async Task Agent_ActualMidLoopContextReplacement_PreservesAdmissionIdentityAndUsesCompactedContext()
    {
        var tool = new ProbeTool();
        var replacementObserved = false;
        var provider = new ScriptedProvider((call, context) =>
        {
            if (call == 2)
            {
                context.SystemPrompt.ShouldBe("compacted-system");
                replacementObserved = true;
            }
            return call == 1 ? Calls(1) : TextResponse();
        });
        var compactions = 0;
        var (agent, handle) = Create(provider, [tool], compaction: _ =>
            Task.FromResult<AgentContext?>(++compactions == 2
                ? new AgentContext("compacted-system", [new CoreUserMessage("compacted-summary")], [tool]) : null));
        await using var owned = handle;
        var events = new List<AgentEvent>();
        using var subscription = agent.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });
        await agent.PromptAsync("original");
        replacementObserved.ShouldBeTrue("prove actual replacement, not merely a compaction callback returning null");
        compactions.ShouldBeGreaterThanOrEqualTo(2);
        tool.Executions.ShouldBe(1);
        var identity = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
        Id(events.OfType<AgentEndEvent>().ShouldHaveSingleItem()).ShouldBe(identity);
        events.ShouldAllBe(evt => Id(evt) == identity);
    }

    [Fact]
    public async Task DirectLoop_RunContinueAndSingleProviderTurn_MintFreshIdsAndStampEveryEvent()
    {
        var provider = new ScriptedProvider((_, _) => TextResponse());
        var model = new LlmModel("direct-model", "Direct", provider.Api, "test-provider", "https://example.test",
            false, ["text"], new ModelCost(0, 0, 0, 0), 128_000, 1024);
        var models = new ModelRegistry();
        models.Register(model.Provider, model);
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var config = new AgentLoopConfig(model, new LlmClient(providers, models),
            (messages, _) => Task.FromResult<IReadOnlyList<Message>>(messages.OfType<CoreUserMessage>()
                .Select(message => (Message)new BotNexus.Agent.Providers.Core.Models.UserMessage(
                    new UserMessageContent(message.Content), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ToArray()),
            null, (_, _) => Task.FromResult<ProviderExecutionOptions?>(null), null, null,
            ToolExecutionMode.Parallel, null, null, new GenerationOptions());
        var events = new List<AgentEvent>();
        Task Emit(AgentEvent evt) { events.Add(evt); return Task.CompletedTask; }
        var identities = new List<string>();
        for (var entry = 0; entry < 3; entry++)
        {
            events.Clear();
            var context = new AgentContext("system", [new CoreUserMessage("existing user")], []);
            _ = await (entry switch
            {
                0 => AgentLoopRunner.RunAsync([new CoreUserMessage("new")], context, config, Emit, CancellationToken.None),
                1 => AgentLoopRunner.ContinueAsync(context, config, Emit, CancellationToken.None),
                _ => AgentLoopRunner.RunSingleProviderTurnAsync(new CoreUserMessage("single"), context, config, Emit, CancellationToken.None)
            });
            var identity = Id(events.OfType<AgentStartEvent>().ShouldHaveSingleItem());
            Id(events.OfType<AgentEndEvent>().ShouldHaveSingleItem()).ShouldBe(identity);
            events.ShouldAllBe(evt => Id(evt) == identity);
            identities.Add(identity);
        }
        identities.ShouldBeUnique();
        provider.Calls.ShouldBe(3);
    }
}
