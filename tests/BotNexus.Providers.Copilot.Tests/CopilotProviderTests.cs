using FluentAssertions;
using BotNexus.Agent.Providers.Copilot;

namespace BotNexus.Providers.Copilot.Tests;

public class CopilotProviderTests
{
    [Fact]
    public void ProviderId_ReturnsGithubCopilot()
    {
        CopilotProvider.ProviderId.Should().Be("github-copilot");
    }

    [Fact]
    public void ResolveApiKey_PrefersConfiguredValue()
    {
        var apiKey = CopilotProvider.ResolveApiKey("configured-token");
        apiKey.Should().Be("configured-token");
    }
}
