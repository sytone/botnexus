using FluentAssertions;
using BotNexus.Providers.OpenAICompat;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class OpenAICompatProviderTests
{
    [Fact]
    public void Api_ReturnsOpenAICompat()
    {
        var provider = new OpenAICompatProvider();

        provider.Api.Should().Be("openai-compat");
    }

    [Fact]
    public void CanConstructProviderInstance()
    {
        var provider = new OpenAICompatProvider();

        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<Core.Registry.IApiProvider>();
    }
}
