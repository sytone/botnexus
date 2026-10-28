using System.Net;
using System.Text.Json;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Copilot.Messages;
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
    [InlineData("messages")]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task AccountTransport_AuthoritativeExecutionThroughRealSseProvider_ProjectsOnlyCurrentInstance(string api)
    {
        var fixture = DetailsFixture();
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var path = Path.Combine(PlatformConfigLoader.GetDefaultConfigDirectory(fs), "auth.json");
        fs.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        void SetAuth(string suffix) => fs.File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["github-copilot"] = new { refresh = "synthetic-oauth-" + suffix, access = "synthetic-session-" + suffix, expires = 4102444800000L, endpoint = "https://api.githubcopilot.com" },
            ["second-auth"] = new { refresh = "synthetic-second-oauth", access = "synthetic-second-session", expires = 4102444800000L, endpoint = "https://api.githubcopilot.com" }
        }));
        SetAuth("first");
        var config = new PlatformConfig { Providers = new() { ["second"] = new() { Type = "github-copilot", ApiKey = "auth:second-auth" } } };
        var options = Substitute.For<IOptionsMonitor<PlatformConfig>>();
        options.CurrentValue.Returns(config);
        var auth = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fs,
            BotNexus.Gateway.Abstractions.Providers.NullProviderHealthObserver.Instance,
            (_, _) => throw new InvalidOperationException("Synthetic credentials must never refresh"));
        var account = new CopilotQuotaService(auth, new CopilotDiscoveryClient(new HttpClient(fixture.Handler)), NullLogger<CopilotQuotaService>.Instance);
        var details = new ProviderUsageDetailsService(auth, account, fixture.Headers, fixture.Cron, options);
        var payload = api switch
        {
            "messages" => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
            "completions" => "data: {\"id\":\"test\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            _ => "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\"}}\n\ndata: [DONE]\n\n"
        };
        var handler = new RecordingHandler(request =>
        {
            request.Headers.Authorization?.Parameter.ShouldBeOneOf("synthetic-session-first", "synthetic-second-session");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream") };
            response.Headers.TryAddWithoutValidation("x-quota-snapshot-chat", request.Headers.Authorization?.Parameter == "synthetic-session-first" ? "rem=42.5&totRem=7.25&private=raw-transport-secret" : "rem=12.5&totRem=2.25");
            return response;
        });
        using var client = new HttpClient(handler);
        IApiProvider provider = api switch
        {
            "messages" => new CopilotMessagesProvider(client, headerSink: fixture.Headers),
            "completions" => new CopilotCompletionsProvider(client, NullLogger<CopilotCompletionsProvider>.Instance, headerSink: fixture.Headers),
            _ => new CopilotResponsesProvider(client, NullLogger<CopilotResponsesProvider>.Instance, headerSink: fixture.Headers)
        };
        var registry = new ApiProviderRegistry();
        registry.Register(provider);
        var llm = new LlmClient(registry, new ModelRegistry());
        foreach (var instance in new[] { "github-copilot", "second" })
        {
            // The gateway supplies both key and attribution; tests never construct a CopilotHeaderScope.
            var execution = await auth.CreateExecutionOptionsAsync(instance);
            (execution.Metadata ?? throw new InvalidOperationException("Gateway omitted attribution metadata")).ShouldContainKey(CopilotHeaderScope.MetadataKey);
            var model = new LlmModel("test", "test", "github-copilot-" + api, instance, "https://api.githubcopilot.com", false, ["text"], new ModelCost(0, 0, 0, 0), 8192, 1024);
            var result = await llm.StreamSimple(model, new Context("test", [new UserMessage("hello", 1)]), new GenerationOptions(), execution)
                .GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
            result.StopReason.ShouldBe(StopReason.Stop, JsonSerializer.Serialize(result));
        }
        var first = await details.ReadAsync();
        first.Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(42.5m);
        first.Headers[0].TotalRemainingCount.ShouldBe(7.25m);
        var second = await details.ReadAsync("second");
        second.Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(12.5m);
        second.Headers[0].TotalRemainingCount.ShouldBe(2.25m);
        first.LegacyUnattributedHeaderResponses.ShouldBe(0);
        first.Account.State.ShouldBe("unavailable");
        Diagnostic(first.Account, "Reason").ShouldBe("not-yet-requested");
        SetAuth("rotated");
        (await details.ReadAsync()).Headers.ShouldBeEmpty();
        (await details.ReadAsync("second")).Headers.ShouldHaveSingleItem().RemainingPercent.ShouldBe(12.5m);
        fs.File.Delete(path);
        var unavailable = await details.ReadAsync();
        unavailable.Headers.ShouldBeEmpty();
        Diagnostic(unavailable.Account, "Reason").ShouldBe("identity-unavailable");
        foreach (var secret in new[] { "synthetic-oauth", "synthetic-session", "synthetic-second", "raw-transport-secret", "Generation", "RequestOrder" })
            JsonSerializer.Serialize(first).ShouldNotContain(secret);
        handler.Requests.Count.ShouldBe(2);
        fixture.Handler.Requests.ShouldBeEmpty(); // local details reads never discover account data
    }
}
