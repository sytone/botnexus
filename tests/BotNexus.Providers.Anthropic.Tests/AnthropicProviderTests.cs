using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

public class AnthropicProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new AnthropicProvider(new HttpClient());

        provider.Api.Should().Be("anthropic-messages");
    }

    [Fact]
    public void StreamSimple_WithOpus4Model_ReturnsStream()
    {
        var provider = new AnthropicProvider(new HttpClient());
        var model = TestHelpers.MakeModel(id: "claude-opus-4.6");
        var context = TestHelpers.MakeContext();
        var options = new Core.SimpleStreamOptions
        {
            Reasoning = ThinkingLevel.High
        };

        // StreamSimple returns a stream immediately (HTTP is async in background)
        var stream = provider.StreamSimple(model, context, options);

        stream.Should().NotBeNull();
    }

    [Fact]
    public void StreamSimple_WithOlderModel_ReturnsStream()
    {
        var provider = new AnthropicProvider(new HttpClient());
        var model = TestHelpers.MakeModel(id: "claude-sonnet-4");
        var context = TestHelpers.MakeContext();
        var options = new Core.SimpleStreamOptions
        {
            Reasoning = ThinkingLevel.Medium
        };

        var stream = provider.StreamSimple(model, context, options);

        stream.Should().NotBeNull();
    }

    [Fact]
    public void StreamSimple_WithoutReasoning_ReturnsStream()
    {
        var provider = new AnthropicProvider(new HttpClient());
        var model = TestHelpers.MakeModel(reasoning: false);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context);

        stream.Should().NotBeNull();
    }
}
