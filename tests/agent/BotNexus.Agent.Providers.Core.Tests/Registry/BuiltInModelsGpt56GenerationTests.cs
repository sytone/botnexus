using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

/// <summary>
/// #3229 clause 1 and 2: the 5.5/5.6 Copilot generation must be present in the built-in table, and
/// each entry must report the SAME <c>supportedThinkingLevels</c> and <c>supportedContextSizes</c>
/// the live discovery overlay reports for it, so a discovered and a built-in registration of the
/// same id agree.
/// </summary>
/// <remarks>
/// The expected values below were read from the running gateway's discovered registry
/// (<c>GET /api/models</c> on 2026-xx): every one of <c>gpt-5.5</c>, <c>gpt-5.6-luna</c>,
/// <c>gpt-5.6-sol</c> and <c>gpt-5.6-terra</c> reports
/// <c>["minimal","low","medium","high","xhigh","max"]</c> and <c>[922000]</c>. A built-in entry
/// that disagrees with discovery is worse than no entry: it silently overrides reality with a
/// wrong context window and nothing reports the difference.
/// </remarks>
public sealed class BuiltInModelsGpt56GenerationTests
{
    private static readonly string[] Gpt56Generation =
    [
        "gpt-5.5",
        "gpt-5.6",
        "gpt-5.6-luna",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
    ];

    /// <summary>The full thinking ladder discovery reports for this generation.</summary>
    private static readonly ThinkingLevel[] FullThinkingLadder =
    [
        ThinkingLevel.Minimal,
        ThinkingLevel.Low,
        ThinkingLevel.Medium,
        ThinkingLevel.High,
        ThinkingLevel.ExtraHigh,
        ThinkingLevel.Max,
    ];

    /// <summary>The context window discovery reports for this generation.</summary>
    private const int DiscoveredContextWindow = 922000;

    private static ModelRegistry BuildRegistry()
    {
        var registry = new ModelRegistry();
        new BuiltInModels().RegisterAll(registry);
        return registry;
    }

    [Fact]
    public void RegisterAll_RegistersTheGpt56Generation_ForGitHubCopilot()
    {
        var registry = BuildRegistry();

        foreach (var id in Gpt56Generation)
        {
            registry.GetModel("github-copilot", id).ShouldNotBeNull(
                $"'{id}' must be a built-in so it resolves without a discovery call (#3229)");
        }
    }

    [Fact]
    public void Gpt56Generation_ContextWindow_MatchesDiscoveredNineTwoTwoK()
    {
        var registry = BuildRegistry();

        foreach (var id in Gpt56Generation)
        {
            var model = registry.GetModel("github-copilot", id);
            model.ShouldNotBeNull();
            model!.ContextWindow.ShouldBe(
                DiscoveredContextWindow,
                $"'{id}' must report the same context window the discovery overlay reports for it");
            ModelRegistry.GetSupportedContextSizes(model).ShouldBe([DiscoveredContextWindow]);
        }
    }

    [Fact]
    public void Gpt56Generation_ThinkingLevels_MatchDiscoveredFullLadder()
    {
        var registry = BuildRegistry();

        foreach (var id in Gpt56Generation)
        {
            var model = registry.GetModel("github-copilot", id);
            model.ShouldNotBeNull();
            model!.Reasoning.ShouldBeTrue($"'{id}' is a reasoning model per the discovery overlay");
            model.SupportsExtraHighThinking.ShouldBeTrue(
                $"'{id}' advertises xhigh/max per the discovery overlay");
            ModelRegistry.GetSupportedThinkingLevels(model).ShouldBe(FullThinkingLadder);
        }
    }
}
