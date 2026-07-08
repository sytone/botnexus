using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Resolution;
using Shouldly;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Unit tests pinning the pure three-layer override precedence
/// (model defaults -&gt; agent -&gt; conversation). Each field is exercised
/// independently to prove most-specific-wins and unset-falls-through.
/// </summary>
public class ModelOverrideResolverTests
{
    private static readonly ModelOverrideLayer Empty = new();

    [Fact]
    public void Resolve_AllLayersEmpty_YieldsAllNull()
    {
        var result = ModelOverrideResolver.Resolve(Empty, Empty, Empty);

        result.Model.ShouldBeNull();
        result.Thinking.ShouldBeNull();
        result.ContextWindow.ShouldBeNull();
    }

    [Fact]
    public void Resolve_OnlyModelDefaults_FallsThroughToDefaults()
    {
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Low, 200_000);

        var result = ModelOverrideResolver.Resolve(defaults, Empty, Empty);

        result.Model.ShouldBe("model-default");
        result.Thinking.ShouldBe(ThinkingLevel.Low);
        result.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void Resolve_AgentBeatsDefaults_WhenAgentSet()
    {
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Low, 200_000);
        var agent = new ModelOverrideLayer("agent-model", ThinkingLevel.High, 1_000_000);

        var result = ModelOverrideResolver.Resolve(defaults, agent, Empty);

        result.Model.ShouldBe("agent-model");
        result.Thinking.ShouldBe(ThinkingLevel.High);
        result.ContextWindow.ShouldBe(1_000_000);
    }

    [Fact]
    public void Resolve_ConversationBeatsAgentAndDefaults_WhenConversationSet()
    {
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Low, 200_000);
        var agent = new ModelOverrideLayer("agent-model", ThinkingLevel.High, 1_000_000);
        var conversation = new ModelOverrideLayer("conv-model", ThinkingLevel.Max, 200_000);

        var result = ModelOverrideResolver.Resolve(defaults, agent, conversation);

        result.Model.ShouldBe("conv-model");
        result.Thinking.ShouldBe(ThinkingLevel.Max);
        result.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void Resolve_ModelField_IsResolvedIndependentlyOfThinkingAndContext()
    {
        // Conversation overrides ONLY the model; thinking + context must fall through
        // to the agent layer (proving each field resolves independently).
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Minimal, 200_000);
        var agent = new ModelOverrideLayer("agent-model", ThinkingLevel.High, 1_000_000);
        var conversation = new ModelOverrideLayer(Model: "conv-model");

        var result = ModelOverrideResolver.Resolve(defaults, agent, conversation);

        result.Model.ShouldBe("conv-model");
        result.Thinking.ShouldBe(ThinkingLevel.High);
        result.ContextWindow.ShouldBe(1_000_000);
    }

    [Fact]
    public void Resolve_ThinkingField_IsResolvedIndependentlyOfModelAndContext()
    {
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Minimal, 200_000);
        var agent = new ModelOverrideLayer("agent-model", ThinkingLevel.High, 1_000_000);
        var conversation = new ModelOverrideLayer(Thinking: ThinkingLevel.Max);

        var result = ModelOverrideResolver.Resolve(defaults, agent, conversation);

        result.Model.ShouldBe("agent-model");
        result.Thinking.ShouldBe(ThinkingLevel.Max);
        result.ContextWindow.ShouldBe(1_000_000);
    }

    [Fact]
    public void Resolve_ContextField_IsResolvedIndependentlyOfModelAndThinking()
    {
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Minimal, 200_000);
        var agent = new ModelOverrideLayer("agent-model", ThinkingLevel.High, 1_000_000);
        var conversation = new ModelOverrideLayer(ContextWindow: 200_000);

        var result = ModelOverrideResolver.Resolve(defaults, agent, conversation);

        result.Model.ShouldBe("agent-model");
        result.Thinking.ShouldBe(ThinkingLevel.High);
        result.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void Resolve_AgentSetsOneField_OthersFallThroughToDefaults()
    {
        // Agent overrides only thinking; model + context fall through to defaults.
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Low, 200_000);
        var agent = new ModelOverrideLayer(Thinking: ThinkingLevel.ExtraHigh);

        var result = ModelOverrideResolver.Resolve(defaults, agent, Empty);

        result.Model.ShouldBe("model-default");
        result.Thinking.ShouldBe(ThinkingLevel.ExtraHigh);
        result.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void Resolve_MixedLayers_EachFieldPicksItsOwnMostSpecificSource()
    {
        // model: from conversation; thinking: from agent; context: from defaults.
        var defaults = new ModelOverrideLayer("model-default", ThinkingLevel.Low, 200_000);
        var agent = new ModelOverrideLayer(Thinking: ThinkingLevel.High);
        var conversation = new ModelOverrideLayer(Model: "conv-model");

        var result = ModelOverrideResolver.Resolve(defaults, agent, conversation);

        result.Model.ShouldBe("conv-model");
        result.Thinking.ShouldBe(ThinkingLevel.High);
        result.ContextWindow.ShouldBe(200_000);
    }
}
