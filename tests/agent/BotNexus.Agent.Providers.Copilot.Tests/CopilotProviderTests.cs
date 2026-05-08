using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Agent.Providers.Copilot.Tests;

public class CopilotProviderTests
{
    [Fact]
    public void ProviderId_ReturnsGithubCopilot()
    {
        CopilotProvider.ProviderId.ShouldBe("github-copilot");
    }

    [Fact]
    public void ResolveApiKey_PrefersConfiguredValue()
    {
        var apiKey = CopilotProvider.ResolveApiKey("configured-token");
        apiKey.ShouldBe("configured-token");
    }
}
