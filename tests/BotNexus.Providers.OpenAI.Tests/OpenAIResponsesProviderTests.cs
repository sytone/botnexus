using BotNexus.Providers.OpenAI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

public class OpenAIResponsesProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        provider.Api.Should().Be("openai-responses");
    }

    [Fact]
    public void StreamSimple_WithNullOptions_DoesNotThrow()
    {
        var provider = new OpenAIResponsesProvider(
            new HttpClient(), NullLogger<OpenAIResponsesProvider>.Instance);

        var model = TestHelpers.MakeModel(id: "gpt-5", api: "openai-responses", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context);

        stream.Should().NotBeNull();
    }
}
