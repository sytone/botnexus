using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

/// <summary>
/// Pins the behaviour of <see cref="CopilotHeaders.BuildDynamicHeaders(System.Collections.Generic.IReadOnlyList{Message}, bool, CopilotHeaderOptions?)"/>
/// — both the wire-parity-preserving default path and the opt-in
/// Copilot-CLI-fidelity overload (Phase 5 of the carve-out, #810).
/// </summary>
public class CopilotHeaderOptionsTests
{
    private static IReadOnlyList<Message> UserMessage() =>
        [new UserMessage("hi", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())];

    [Fact]
    public void NullOptions_EmitsOnlyLegacyHeaderSet()
    {
        var headers = CopilotHeaders.BuildDynamicHeaders(UserMessage(), hasImages: false, options: null);

        headers.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            ["Openai-Intent", "X-Initiator"]);
        headers["Openai-Intent"].ShouldBe("conversation-edits");
        headers["X-Initiator"].ShouldBe("user");
    }

    [Fact]
    public void NullOptions_AndImages_AddsCopilotVisionRequest()
    {
        var headers = CopilotHeaders.BuildDynamicHeaders(UserMessage(), hasImages: true, options: null);

        headers["Copilot-Vision-Request"].ShouldBe("true");
        headers.ShouldNotContainKey("Copilot-Integration-Id");
    }

    [Fact]
    public void OptionsPopulated_EmitsExtraCopilotCliHeaders()
    {
        var options = new CopilotHeaderOptions(
            IntegrationId: "copilot-developer-cli",
            ApiVersion: "2026-06-01",
            EditorVersion: "BotNexus/0.1.0",
            InteractionId: "00000000-0000-0000-0000-000000000001");

        var headers = CopilotHeaders.BuildDynamicHeaders(UserMessage(), hasImages: false, options);

        headers["Copilot-Integration-Id"].ShouldBe("copilot-developer-cli");
        headers["X-GitHub-Api-Version"].ShouldBe("2026-06-01");
        headers["Editor-Version"].ShouldBe("BotNexus/0.1.0");
        headers["X-Interaction-Id"].ShouldBe("00000000-0000-0000-0000-000000000001");
        // Legacy header set still present.
        headers["X-Initiator"].ShouldBe("user");
        headers["Openai-Intent"].ShouldBe("conversation-edits");
    }

    [Fact]
    public void IntentOverride_ReplacesDefaultOpenaiIntent()
    {
        var options = new CopilotHeaderOptions(IntentOverride: "conversation-agent");

        var headers = CopilotHeaders.BuildDynamicHeaders(UserMessage(), hasImages: false, options);

        headers["Openai-Intent"].ShouldBe("conversation-agent");
    }

    [Fact]
    public void EmptyStringFields_AreNotEmitted()
    {
        var options = new CopilotHeaderOptions(
            IntegrationId: "",
            ApiVersion: "",
            EditorVersion: "",
            InteractionId: "",
            IntentOverride: "");

        var headers = CopilotHeaders.BuildDynamicHeaders(UserMessage(), hasImages: false, options);

        headers.ShouldNotContainKey("Copilot-Integration-Id");
        headers.ShouldNotContainKey("X-GitHub-Api-Version");
        headers.ShouldNotContainKey("Editor-Version");
        headers.ShouldNotContainKey("X-Interaction-Id");
        headers["Openai-Intent"].ShouldBe("conversation-edits"); // empty override → default
    }
}
