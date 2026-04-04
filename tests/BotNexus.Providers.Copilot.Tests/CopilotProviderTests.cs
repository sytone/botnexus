using FluentAssertions;
using BotNexus.Providers.Copilot;

namespace BotNexus.Providers.Copilot.Tests;

public class CopilotProviderTests
{
    [Fact]
    public void Api_ReturnsGithubCopilot()
    {
        var provider = new CopilotProvider();

        provider.Api.Should().Be("github-copilot");
    }

    [Fact]
    public void CanConstructProviderInstance()
    {
        var provider = new CopilotProvider();

        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<Core.Registry.IApiProvider>();
    }
}
