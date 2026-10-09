using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

public sealed class CopilotHeaderConcurrencyTests
{
    [Theory]
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task ComposedRequests_OutOfOrderTwoInstancesAndRotation_RemainIsolated(string api)
    {
        var handler = new ControlledHandler();
        using var client = new HttpClient(handler);
        var store = new CopilotHeaderQuotaStore();
        var providers = new ApiProviderRegistry();
        providers.Register(api switch
        {
            "messages" => new CopilotMessagesProvider(client, headerSink: store),
            "completions" => new CopilotCompletionsProvider(client, NullLogger<CopilotCompletionsProvider>.Instance, headerSink: store),
            _ => new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, headerSink: store)
        });
        var llm = new LlmClient(providers, new ModelRegistry());
        var a = new CopilotHeaderScope("a", "old");
        var b = new CopilotHeaderScope("b", "current");
        var rotated = new CopilotHeaderScope("a", "new");
        LlmStream Start(CopilotHeaderScope scope) => llm.Stream(new LlmModel("test", "test", "github-copilot-" + api,
            scope.Instance, "https://api.githubcopilot.com", false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024),
            new Context("test", [new UserMessage("hello", 1)]),
            new StreamOptions { ApiKey = "synthetic", Metadata = new() { [CopilotHeaderScope.MetadataKey] = scope } });
        var first = Start(a);
        await handler.Started[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Start(a);
        await handler.Started[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var other = Start(b);
        await handler.Started[2].Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rotation = Start(rotated);
        await handler.Started[3].Task.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Complete(3, "rem=30");
        _ = await rotation.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        handler.Complete(2, "rem=40");
        _ = await other.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        handler.Complete(1, "rem=20");
        _ = await second.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        handler.Complete(0, "rem=80");
        _ = await first.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.GetLatest(a, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(20m);
        store.GetLatest(b, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(40m);
        store.GetLatest(rotated, CopilotQuotaDimension.Chat)?.Quota.RemainingPercent.ShouldBe(30m);
        store.Count.ShouldBe(3);
        store.GetLatest(new("unknown", "old"), CopilotQuotaDimension.Chat).ShouldBeNull();
    }

    private sealed class ControlledHandler : HttpMessageHandler
    {
        private int _index = -1;
        public TaskCompletionSource<bool>[] Started { get; } = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource<HttpResponseMessage>[] _responses = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index);
            Started[index].SetResult(true);
            return _responses[index].Task;
        }
        public void Complete(int index, string quota)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"rejected\"}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", quota);
            _responses[index].SetResult(response);
        }
    }
}
