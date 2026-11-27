using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SystemPromptBuilderMessagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildMessagingSection_FullMode_PreservesInternalRoutingWithoutScriptBypasses(bool hasMessageTool)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasMessageTool)
            tools.Add("message");

        var text = string.Join('\n', SystemPromptBuilder.BuildMessagingSection(
            isMinimal: false, tools, runtimeChannel: "signalr", inlineButtonsEnabled: false));

        text.ShouldContain("LLM-provider requests, assistant replies to users, and agent-to-agent communication");
        text.ShouldContain("must use BotNexus tools and internal routing");
        text.ShouldContain("never bypass this routing with exec, curl, or other shell/scripts");
        text.ShouldNotContain("Never use exec/curl for provider messaging;");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildMessagingSection_FullMode_DistinguishesAuthorizedIntegrationsWithoutGrantingShellSending(bool hasMessageTool)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasMessageTool)
            tools.Add("message");

        var text = string.Join('\n', SystemPromptBuilder.BuildMessagingSection(
            isMinimal: false, tools, runtimeChannel: "signalr", inlineButtonsEnabled: false));

        text.ShouldContain("Separately user-authorized SMS, iMessage, or email integrations are not LLM-provider messaging");
        text.ShouldContain("use only available sanctioned integration tools");
        text.ShouldContain("recipient and message authorization, credentials, and verification");
        text.ShouldContain("does not authorize unrestricted shell sending or bypassing BotNexus routing");
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
