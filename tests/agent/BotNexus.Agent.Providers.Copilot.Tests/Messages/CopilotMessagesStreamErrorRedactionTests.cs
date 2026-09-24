using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Copilot.Tests.Messages;

public sealed class CopilotMessagesStreamErrorRedactionTests
{
    private const string Token = "synthetic-stream-token-4119";

    [Fact]
    public async Task StreamedError_RedactsPeerMessageAndPreservesContext()
    {
        const string sse = "event: error\ndata: {\"type\":\"error\",\"error\":{\"message\":\"quota rejected synthetic-stream-token-4119\"}}\n\n";
        var provider = new CopilotMessagesProvider(
            new HttpClient(new Handler(sse)), new StubRedactor());

        var result = await provider.Stream(Model(), Context(), new CopilotMessagesOptions { ApiKey = "test" })
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var error = result.ErrorMessage;
        error.ShouldBe("Copilot streaming error: quota rejected [REDACTED]");
        error.ShouldNotBeNull();
        error.ShouldNotContain(Token);
    }

    [Fact]
    public async Task StreamedError_WithoutRedactor_PreservesExistingMessage()
    {
        const string sse = "event: error\ndata: {\"type\":\"error\",\"error\":{\"message\":\"plain peer detail\"}}\n\n";
        var provider = new CopilotMessagesProvider(new HttpClient(new Handler(sse)));

        var result = await provider.Stream(Model(), Context(), new CopilotMessagesOptions { ApiKey = "test" })
            .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));

        result.ErrorMessage.ShouldBe("Copilot streaming error: plain peer detail");
    }

    private static LlmModel Model() => new("claude-test", "claude-test", CopilotMessagesProvider.ApiId,
        "github-copilot", "https://example.test", false, ["text"], new ModelCost(0, 0, 0, 0), 1000, 100);
    private static Context Context() => new("system", [new UserMessage(new UserMessageContent("hello"), 1)], []);

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    private sealed class StubRedactor : ISecretRedactor
    {
        public string Redact(string input) => input.Replace(Token, "[REDACTED]", StringComparison.Ordinal);
        public string RedactForExternalDelivery(string input) => Redact(input);
    }
}
