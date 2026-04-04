using BotNexus.Providers.OpenAI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

public class OpenAICompletionsProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAICompletionsProvider(
            new HttpClient(), NullLogger<OpenAICompletionsProvider>.Instance);

        provider.Api.Should().Be("openai-completions");
    }

    [Fact]
    public void StreamSimple_WithNullOptions_DoesNotThrow()
    {
        // StreamSimple should construct options and delegate to Stream.
        // The actual HTTP call will fail, but construction must succeed.
        var provider = new OpenAICompletionsProvider(
            new HttpClient(), NullLogger<OpenAICompletionsProvider>.Instance);

        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        // StreamSimple returns an LlmStream immediately (HTTP call is async in background)
        var stream = provider.StreamSimple(model, context);

        stream.Should().NotBeNull();
    }
}
