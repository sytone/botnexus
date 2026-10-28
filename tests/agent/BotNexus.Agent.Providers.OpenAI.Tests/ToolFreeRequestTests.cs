using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

public sealed class ToolFreeRequestTests
{
    [Fact]
    public async Task StreamSimple_DisableIntentPhysicallyRemovesToolsAndSetsNone()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAIResponsesProvider(client, NullLogger<OpenAIResponsesProvider>.Instance);
        var context = TestHelpers.MakeContext() with
        {
            Tools = [new BotNexus.Agent.Providers.Core.Models.Tool("forbidden", "must not be advertised",
                JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]
        };
        var stream = provider.StreamSimple(TestHelpers.MakeModel(api: "openai-responses"), context,
            new SimpleStreamOptions { ApiKey = "test-key", ToolChoiceMode = ToolChoice.None });
        _ = await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var body = JsonDocument.Parse(handler.Body.ShouldNotBeNull());
        body.RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
        body.RootElement.GetProperty("tool_choice").GetString().ShouldBe("none");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
