using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SystemPromptBuilderMessagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildMessagingSection_FullMode_AllowsPlatformApiAutomationButRequiresPlatformLlmProviders(bool hasMessageTool)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasMessageTool)
            tools.Add("message");

        var text = string.Join('\n', SystemPromptBuilder.BuildMessagingSection(
            isMinimal: false, tools, runtimeChannel: "signalr", inlineButtonsEnabled: false));

        text.ShouldContain("LLM requests must use BotNexus platform LLM providers, not direct calls to external LLM provider APIs");
        text.ShouldContain("Skills, agents, and scripts may use the BotNexus API to automate interactions");
        text.ShouldNotContain("never bypass this routing with exec, curl, or other shell/scripts");
        text.ShouldNotContain("Never use exec/curl for provider messaging;");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildMessagingSection_FullMode_DistinguishesAuthorizedIntegrations(bool hasMessageTool)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasMessageTool)
            tools.Add("message");

        var text = string.Join('\n', SystemPromptBuilder.BuildMessagingSection(
            isMinimal: false, tools, runtimeChannel: "signalr", inlineButtonsEnabled: false));

        text.ShouldContain("Separately user-authorized SMS, iMessage, or email integrations are not LLM-provider messaging");
        text.ShouldContain("use available sanctioned integration tools or the BotNexus API where supported");
        text.ShouldContain("recipient and message authorization, credentials, and verification");
        text.ShouldNotContain("This distinction does not authorize unrestricted shell sending");
        text.ShouldContain("subject to security safeguards");
        text.ShouldContain("verification of delivery");
    }

    [Fact]
    public void BuildMessagingSection_MinimalMode_RemainsEmpty()
    {
        var lines = SystemPromptBuilder.BuildMessagingSection(
            isMinimal: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "message" },
            runtimeChannel: "signalr",
            inlineButtonsEnabled: false);

        lines.ShouldBeEmpty();
    }
}
