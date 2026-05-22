using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Scenarios.Harness;

namespace BotNexus.Scenarios.Tests.Adapter;

/// <summary>
/// Conformance scenarios for <see cref="ScenarioFakeApiProvider"/>: proves the deterministic
/// LLM driver behaves as the scenario suite expects (turn counting, scripted replies,
/// per-turn context observation, registry wiring).
/// </summary>
public sealed class ScenarioFakeApiProviderConformance
{
    private static readonly LlmModel Model = ScenarioFakeApiProvider.CreateModel();
    private static readonly Context EmptyContext = new(SystemPrompt: null, Messages: []);

    [Fact]
    public async Task Constant_Response_IsReturnedForEveryTurn()
    {
        var provider = new ScenarioFakeApiProvider("hello citizen");

        var first = await provider.Stream(Model, EmptyContext).GetResultAsync();
        var second = await provider.StreamSimple(Model, EmptyContext).GetResultAsync();

        TextOf(first).ShouldBe("hello citizen");
        TextOf(second).ShouldBe("hello citizen");
        provider.TurnCount.ShouldBe(2);
    }

    [Fact]
    public async Task ScriptedFactory_SeesZeroBasedTurnIndex_AndReturnsPerTurnText()
    {
        var observed = new List<int>();
        var provider = new ScenarioFakeApiProvider((turn, _) =>
        {
            observed.Add(turn);
            return $"turn-{turn}";
        });

        var t0 = await provider.StreamSimple(Model, EmptyContext).GetResultAsync();
        var t1 = await provider.StreamSimple(Model, EmptyContext).GetResultAsync();
        var t2 = await provider.StreamSimple(Model, EmptyContext).GetResultAsync();

        observed.ShouldBe([0, 1, 2]);
        TextOf(t0).ShouldBe("turn-0");
        TextOf(t1).ShouldBe("turn-1");
        TextOf(t2).ShouldBe("turn-2");
    }

    [Fact]
    public async Task Provider_TagsAssistantMessage_WithScenarioApiAndModelMetadata()
    {
        var provider = new ScenarioFakeApiProvider("anything");

        var message = await provider.StreamSimple(Model, EmptyContext).GetResultAsync();

        message.Api.ShouldBe(ScenarioFakeApiProvider.ApiName);
        message.ModelId.ShouldBe(ScenarioFakeApiProvider.ModelId);
        message.Provider.ShouldBe(ScenarioFakeApiProvider.ProviderName);
        message.StopReason.ShouldBe(StopReason.Stop);
    }

    [Fact]
    public async Task Register_WiresProviderAndModel_IntoRegistries_AndIsRoutedByLlmClient()
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        var provider = new ScenarioFakeApiProvider("registered reply");

        provider.Register(providers, models);

        providers.Get(ScenarioFakeApiProvider.ApiName).ShouldNotBeNull();
        var resolvedModel = models.GetModel(ScenarioFakeApiProvider.ProviderName, ScenarioFakeApiProvider.ModelId);
        resolvedModel.ShouldNotBeNull();
        resolvedModel.Api.ShouldBe(ScenarioFakeApiProvider.ApiName);

        var llmClient = new LlmClient(providers, models);
        var reply = await llmClient.CompleteSimpleAsync(resolvedModel, EmptyContext);

        TextOf(reply).ShouldBe("registered reply");
    }

    [Fact]
    public async Task Factory_ReceivesGatewayAssembledContext_SoScenariosCanAssertOnSystemPromptAndHistory()
    {
        Context? captured = null;
        var provider = new ScenarioFakeApiProvider((_, ctx) =>
        {
            captured = ctx;
            return "ack";
        });

        var context = new Context(SystemPrompt: "you are agent-a", Messages: []);
        _ = await provider.StreamSimple(Model, context).GetResultAsync();

        captured.ShouldNotBeNull();
        captured.SystemPrompt.ShouldBe("you are agent-a");
    }

    private static string TextOf(AssistantMessage message)
        => string.Concat(message.Content.OfType<TextContent>().Select(t => t.Text));
}
