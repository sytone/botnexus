using System.Net;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Responses;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Api.Tests;

public sealed partial class CopilotQuotaControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountWebSocket_AuthoritativeSupplierThroughRealProvider_IsolatesInstancesAndLateOldGeneration(bool reject)
    {
        var fixture = DetailsFixture();
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        void SetAuth(string suffix) => fs.File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["work-a"] = new { refresh = "synthetic-ws-oauth-" + suffix, access = "synthetic-ws-session-" + suffix, expires = 4102444800000L, endpoint = "https://api.githubcopilot.com" },
            ["reference-b"] = new { refresh = "synthetic-ws-oauth-b", access = "synthetic-ws-session-b", expires = 4102444800000L, endpoint = "https://api.githubcopilot.com" }
        }));
        SetAuth("first");
        var config = new PlatformConfig { Providers = new()
        {
            // A usable exact entry must win even though both instances declare the other account.
            ["work-a"] = new() { Type = "github-copilot", ApiKey = "auth:reference-b" },
            ["work-b"] = new() { Type = "github-copilot", ApiKey = "auth:reference-b" }
        } };
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(config);
        var refreshCalls = 0;
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs,
            BotNexus.Gateway.Abstractions.Providers.NullProviderHealthObserver.Instance,
            (_, _) => { refreshCalls++; throw new InvalidOperationException("Synthetic auth must never refresh"); });
        var http = new RecordingHandler(_ => throw new InvalidOperationException("No HTTP inference, fallback, or discovery is allowed"));
        using var client = new HttpClient(http);
        var account = new CopilotQuotaService(auth, new CopilotDiscoveryClient(client), NullLogger<CopilotQuotaService>.Instance);
        var details = new ProviderUsageDetailsService(auth, account, fixture.Headers, fixture.Cron, options);

        Task<AssistantMessage> Start(string instance, ProviderExecutionOptions execution, AccountWebSocket socket)
        {
            var provider = new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, socket, headerSink: fixture.Headers);
            var registry = new ApiProviderRegistry();
            registry.Register(provider);
            var model = new LlmModel("issue4923-websocket-composition", "test", provider.Api, instance,
                "https://api.githubcopilot.com", false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
            // Exercise normal StreamSimple policy selection, not a handcrafted provider options copy.
            CopilotResolvedModelDescriptors.Set(model, ["/responses", "ws:/responses"]);
            return new LlmClient(registry, new ModelRegistry()).StreamSimple(model,
                new Context("test", [new UserMessage("hello", 1)]), new GenerationOptions(), execution)
                .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        async Task Finish(Task<AssistantMessage> pending, AccountWebSocket socket)
        {
            var result = await pending;
            result.StopReason.ShouldBe(reject ? StopReason.Error : StopReason.Stop, JsonSerializer.Serialize(result));
            await socket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            socket.Sends.ShouldBe(reject ? 0 : 1);
            socket.Receives.ShouldBe(reject ? 0 : 1);
        }
        AccountWebSocket Socket(string key, string quota, bool hold = false) => new(reject, key, quota,
            () => fixture.Headers.Count.ShouldBeGreaterThan(0), hold);

        // Only GatewayAuthManager constructs attribution. Observe, but never seed or replace it.
        var firstExecution = await auth.CreateExecutionOptionsAsync("work-a");
        firstExecution.ApiKey.ShouldBe("synthetic-ws-session-first");
        (firstExecution.Metadata ?? throw new InvalidOperationException("Missing gateway metadata"))
            [CopilotHeaderScope.MetadataKey].ShouldBeOfType<CopilotHeaderScope>().Instance.ShouldBe("work-a");
        var firstSocket = Socket("synthetic-ws-session-first", "rem=42.5&totRem=7.25&private=raw-ws-secret");
        await Finish(Start("work-a", firstExecution, firstSocket), firstSocket);
        var secondExecution = await auth.CreateExecutionOptionsAsync("work-b");
        secondExecution.ApiKey.ShouldBe("synthetic-ws-session-b");
        var secondSocket = Socket("synthetic-ws-session-b", "rem=12.5&totRem=2.25");
        await Finish(Start("work-b", secondExecution, secondSocket), secondSocket);
        fixture.Headers.Count.ShouldBe(2);
        var first = await details.ReadAsync("work-a");
        var second = await details.ReadAsync("work-b");
        first.Instance.ShouldBe("work-a");
        first.Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(42.5m);
        first.Headers[0].TotalRemainingCount.ShouldBe(7.25m);
        second.Instance.ShouldBe("work-b");
        second.Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(12.5m);
        second.Headers[0].TotalRemainingCount.ShouldBe(2.25m);
        first.Account.State.ShouldBe("unavailable");
        Diagnostic(first.Account, "Reason").ShouldBe("not-yet-requested");
        first.LegacyUnattributedHeaderResponses.ShouldBe(0);
        second.LegacyUnattributedHeaderResponses.ShouldBe(0);

        // An old handshake arrives only after rotation and a new-generation observation.
        var lateSocket = Socket("synthetic-ws-session-first", "rem=99&totRem=99", hold: true);
        var late = Start("work-a", await auth.CreateExecutionOptionsAsync("work-a"), lateSocket);
        await lateSocket.Connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            SetAuth("rotated-longer");
            auth.InvalidateCache();
            var rotated = await details.ReadAsync("work-a");
            rotated.Headers.ShouldBeEmpty();
            (await details.ReadAsync("work-b")).Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(12.5m);
            var rotatedExecution = await auth.CreateExecutionOptionsAsync("work-a");
            rotatedExecution.ApiKey.ShouldBe("synthetic-ws-session-rotated-longer");
            var rotatedSocket = Socket("synthetic-ws-session-rotated-longer", "rem=25.5&totRem=3.75");
            await Finish(Start("work-a", rotatedExecution, rotatedSocket), rotatedSocket);
        }
        finally { lateSocket.Release.TrySetResult(); }
        await Finish(late, lateSocket);
        var current = await details.ReadAsync("work-a");
        current.Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(25.5m);
        current.Headers[0].TotalRemainingCount.ShouldBe(3.75m);
        (await details.ReadAsync("work-b")).Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(12.5m);

        fs.File.Delete(path);
        auth.InvalidateCache();
        var removed = await details.ReadAsync("work-a");
        removed.Headers.ShouldBeEmpty();
        Diagnostic(removed.Account, "Reason").ShouldBe("identity-unavailable");
        fixture.Headers.LegacyUnattributedResponses.ShouldBe(0);
        refreshCalls.ShouldBe(0);
        http.Requests.ShouldBeEmpty();
        fixture.Handler.Requests.ShouldBeEmpty();
        var projected = JsonSerializer.Serialize(new[] { first, second, current, removed });
        foreach (var secret in new[] { "synthetic-ws", "raw-ws-secret", "reference-b", "Authorization", "Generation", "RequestOrder", "api.githubcopilot.com" })
            projected.ShouldNotContain(secret);
    }

    private sealed class AccountWebSocket(bool reject, string expectedKey, string quota, Action beforeSend, bool hold)
        : ICopilotResponsesWebSocketTransport
    {
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends { get; private set; }
        public int Receives { get; private set; }
        public CopilotResponsesCloseFrame? LastClose => null;
        public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders { get; } = new Dictionary<string, IEnumerable<string>>
        {
            ["x-quota-snapshot-chat"] = [quota],
            ["x-private-header"] = ["raw-ws-secret"]
        };
        public async ValueTask ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            uri.AbsoluteUri.ShouldBe("wss://api.githubcopilot.com/responses");
            headers["Authorization"].ShouldBe("Bearer " + expectedKey);
            Connected.TrySetResult();
            if (hold) await Release.Task.WaitAsync(cancellationToken);
            if (reject) throw new CopilotResponsesWebSocketHandshakeException((int)HttpStatusCode.Forbidden, "Synthetic rejected handshake", null);
        }
        public ValueTask SendAsync(string payload, CancellationToken cancellationToken)
        {
            beforeSend();
            using var document = JsonDocument.Parse(payload);
            document.RootElement.GetProperty("type").GetString().ShouldBe("response.create");
            Sends++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            Receives++;
            return ValueTask.FromResult<string?>("{\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\"}}");
        }
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
