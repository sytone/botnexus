using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Issue #2874: the injected runtime context block described an off|on|stream reasoning DISPLAY
/// mode that no code implements. The client's thinking visibility (<c>ShowThinking</c>) is a bool
/// defaulting to <see langword="true"/> applied as a CSS filter in ChatPanel.razor, and
/// <c>/reasoning</c> is a per-conversation thinking-LEVEL override, not a visibility toggle.
/// These tests pin the block's reasoning claims to the value actually resolved from the
/// descriptor via <see cref="EffectiveExecutionSettings.ThinkingWireToken"/>.
/// </summary>
public sealed class RuntimeReasoningBlockTests
{
    private static string BuildPrompt(string? reasoningLevel) =>
        SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "agent-a",
                Channel = "signalr"
            },
            ReasoningLevel = reasoningLevel
        });

    private static string? ReasoningLine(string prompt) =>
        prompt
            .Replace("\r\n", "\n")
            .Split('\n')
            .FirstOrDefault(static line => line.StartsWith("Reasoning:", StringComparison.Ordinal));

    [Theory]
    [InlineData(ThinkingLevel.Minimal)]
    [InlineData(ThinkingLevel.Low)]
    [InlineData(ThinkingLevel.Medium)]
    [InlineData(ThinkingLevel.High)]
    [InlineData(ThinkingLevel.ExtraHigh)]
    [InlineData(ThinkingLevel.Max)]
    public void RuntimeBlock_ReportsResolvedDescriptorThinkingLevel(ThinkingLevel level)
    {
        // AC2/AC6: the wording must carry the level the descriptor actually resolves to, not a
        // hand-written literal. Derive the expectation from the same seam the gateway uses.
        var settings = new EffectiveExecutionSettings(
            Provider: "openai",
            Model: "gpt-5",
            DescriptorDefaultModel: null,
            Thinking: level,
            ContextWindow: null);

        var token = settings.ThinkingWireToken;
        token.ShouldNotBeNull();

        var line = ReasoningLine(BuildPrompt(token));

        line.ShouldNotBeNull();
        line!.Contains(token!, StringComparison.Ordinal).ShouldBeTrue(
            $"the runtime block must report the resolved thinking level '{token}', but said: {line}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    public void RuntimeBlock_OmitsReasoningLine_WhenLevelUnresolvable(string? reasoningLevel)
    {
        // AC2: an unresolved level must be omitted rather than reported as a fabricated "off"
        // display mode. ThinkingWireToken returns null when no level is set.
        new EffectiveExecutionSettings(null, null, null, null, null).ThinkingWireToken.ShouldBeNull();

        ReasoningLine(BuildPrompt(reasoningLevel)).ShouldBeNull();
    }

    [Fact]
    public void RuntimeBlock_MakesNoDisplayModeOrToggleClaims()
    {
        // AC1/AC3/AC4/AC5: no off|on|stream display mode, no claim that /reasoning toggles
        // visibility, no claim that /status reports a reasoning display state, and no claim about
        // client rendering that ChatPanel.razor does not implement.
        var prompt = BuildPrompt("medium");
        var line = ReasoningLine(prompt);
        line.ShouldNotBeNull();

        foreach (var forbidden in new[]
                 {
                     "hidden unless",
                     "on/stream",
                     "Toggle /reasoning",
                     "/status shows Reasoning",
                     "when enabled"
                 })
        {
            line!.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                $"the runtime block must not claim '{forbidden}': {line}");
        }
    }
}
