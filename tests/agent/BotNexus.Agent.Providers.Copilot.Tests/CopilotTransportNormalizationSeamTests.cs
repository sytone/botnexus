using System.Reflection;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Fence proving all three Copilot transports apply the SAME text-delta normalization (#2443).
/// </summary>
/// <remarks>
/// This is deliberately a wiring test, not a behaviour test. Behaviour tests on each transport can
/// all stay green while a fourth transport is added with no normalization at all - which is exactly
/// how #2170 happened after #2049. The property that must hold is "every Copilot transport routes
/// through <see cref="CopilotTextDeltaNormalizer"/>", and only inspecting the wiring can check it.
/// </remarks>
public class CopilotTransportNormalizationSeamTests
{
    private static LlmModel Model(string id) => new(
        Id: id,
        Name: id,
        Api: "github-copilot-completions",
        Provider: "github-copilot",
        BaseUrl: "https://api.enterprise.githubcopilot.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384);

    private static CompletionsTransportProfile BuildCompletionsProfile()
    {
        var method = typeof(CopilotCompletionsProvider).GetMethod(
            "BuildProfile", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull("CopilotCompletionsProvider must still build its transport profile.");

        return (CompletionsTransportProfile)method!.Invoke(null, [null])!;
    }

    // The Completions transport was the unnormalized one. If this hook is ever dropped, the third
    // recurrence of the CRLF family is one model-discovery decision away.
    [Fact]
    public void CompletionsProfile_DeclaresTheTextDeltaNormalizer()
    {
        var profile = BuildCompletionsProfile();

        profile.NormalizeTextDelta.ShouldNotBeNull(
            "The Copilot Completions transport must apply the same text-delta normalization as " +
            "Responses and Messages (#2443).");
    }

    // Prove the hook is the shared normalizer, not a hand-rolled copy with drifting semantics:
    // it must reproduce the normalizer's behaviour on both a fire and a no-fire case.
    [Fact]
    public void CompletionsProfileHook_BehavesIdenticallyToTheSharedNormalizer()
    {
        var hook = BuildCompletionsProfile().NormalizeTextDelta!;

        hook(Model("gpt-5.6"), "\r\n\r\nHello")
            .ShouldBe(CopilotTextDeltaNormalizer.Normalize("gpt-5.6", "\r\n\r\nHello"));

        hook(Model("claude-sonnet-4"), "\r\nuntouched")
            .ShouldBe(CopilotTextDeltaNormalizer.Normalize("claude-sonnet-4", "\r\nuntouched"));
    }

    // Non-vacuity: the seam must actually transform something on the fire case, otherwise the
    // equality assertion above would hold trivially for a no-op hook.
    [Fact]
    public void CompletionsProfileHook_ActuallyStripsOnTheFireCase()
        => BuildCompletionsProfile().NormalizeTextDelta!(Model("gpt-5.6"), "\r\nHello")
            .ShouldBe("Hello");

    // The Copilot provider assembly must contain exactly one type performing this normalization.
    [Fact]
    public void CopilotAssembly_HasExactlyOneTextDeltaNormalizer()
    {
        var normalizers = typeof(CopilotTextDeltaNormalizer).Assembly
            .GetTypes()
            .Where(t => t.Name.Contains("TextDeltaNormalizer", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        normalizers.ShouldBe([typeof(CopilotTextDeltaNormalizer).FullName!]);
    }
}
