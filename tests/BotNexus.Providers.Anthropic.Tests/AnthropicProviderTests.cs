using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core.Models;
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

    [Fact]
    public async Task StreamSimple_MinimalThinking_UsesDefaultBudget1024()
    {
        var handler = new RecordingHandler();
        var provider = new AnthropicProvider(new HttpClient(handler));
        var model = TestHelpers.MakeModel(id: "claude-3-5-haiku", reasoning: true);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context, new Core.SimpleStreamOptions
        {
            ApiKey = "test-key",
            Reasoning = ThinkingLevel.Minimal
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement
            .GetProperty("thinking")
            .GetProperty("budget_tokens")
            .GetInt32()
            .Should()
            .Be(1024);
    }

    [Theory]
    [InlineData("claude-opus-4-6", "max")]
    [InlineData("claude-opus-4.6", "max")]
    [InlineData("claude-sonnet-4-5", "high")]
    [InlineData("claude-haiku-4-5", "high")]
    public async Task StreamSimple_ExtraHigh_UsesExpectedReasoningGuard(string modelId, string expectedEffort)
    {
        var handler = new RecordingHandler();
        var provider = new AnthropicProvider(new HttpClient(handler));
        // Use high maxTokens so thinking budget isn't clamped by model limits
        var model = TestHelpers.MakeModel(id: modelId, reasoning: true, maxTokens: 200000);
        var context = TestHelpers.MakeContext();

        var stream = provider.StreamSimple(model, context, new Core.SimpleStreamOptions
        {
            ApiKey = "test-key",
            Reasoning = ThinkingLevel.ExtraHigh
        });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        if (body.RootElement.TryGetProperty("output_config", out var outputConfig) &&
            outputConfig.TryGetProperty("effort", out var effort))
        {
            effort.GetString().Should().Be(expectedEffort);
            return;
        }

        body.RootElement
            .GetProperty("thinking")
            .GetProperty("budget_tokens")
            .GetInt32()
            .Should()
            .Be(16384);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad request", Encoding.UTF8, "application/json")
            };
        }
    }
}
