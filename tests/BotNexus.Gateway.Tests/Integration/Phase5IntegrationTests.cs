using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Channels.Core;
using BotNexus.Channels.Tui;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class Phase5IntegrationTests
{
    private static readonly string CopilotAuthPath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        ".botnexus-agent",
        "auth.json");

    [Fact]
    public async Task AuthenticatedAndUnauthenticated_ApiAgentsRequests_BehaveAsExpected()
    {
        await using var host = await GatewayApiHarness.StartAsync("phase5-key");

        var unauthenticated = await host.Client.GetAsync("/api/agents");
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        request.Headers.Add("X-Api-Key", "phase5-key");
        var authenticated = await host.Client.SendAsync(request);

        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SessionLifecycle_TransitionsToExpired_WhenIdleBeyondTtl()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("phase5-session", "agent-a");
        session.Status = GatewaySessionStatus.Active;
        session.UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await store.SaveAsync(session);

        var cleanup = new SessionCleanupService(
            store,
            Options.Create(new SessionCleanupOptions { SessionTtl = TimeSpan.FromMinutes(30) }),
            NullLogger<SessionCleanupService>.Instance);

        await cleanup.RunCleanupOnceAsync();

        var reloaded = await store.GetAsync("phase5-session");
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(GatewaySessionStatus.Expired);
        reloaded.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public void ChannelManager_ReportsTuiCapabilities()
    {
        IChannelManager manager = new ChannelManager([new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance)]);

        var adapter = manager.Get("tui");
        adapter.Should().NotBeNull();
        adapter!.SupportsSteering.Should().BeTrue();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigValidationEndpoint_ReturnsExpectedValidationResults()
    {
        var controller = new ConfigController();
        var root = Path.Combine(AppContext.BaseDirectory, "phase5-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var validPath = Path.Combine(root, "config.json");
        var missingPath = Path.Combine(root, "missing.json");

        try
        {
            await File.WriteAllTextAsync(validPath, """
                {
                  "providers": {
                    "copilot": {
                      "apiKey": "test-key",
                      "baseUrl": "https://api.githubcopilot.com",
                      "defaultModel": "gpt-4.1"
                    }
                  }
                }
                """);

            var validResult = await controller.Validate(validPath, CancellationToken.None);
            var validPayload = validResult.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<ConfigValidationResponse>().Subject;
            validPayload.IsValid.Should().BeTrue();
            validPayload.Errors.Should().BeEmpty();

            var missingResult = await controller.Validate(missingPath, CancellationToken.None);
            var missingPayload = missingResult.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<ConfigValidationResponse>().Subject;
            missingPayload.IsValid.Should().BeFalse();
            missingPayload.Errors.Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task CopilotAuthConfigured_LiveStreamingMessage_ReturnsDeltas()
    {
        if (!ShouldRunLiveIntegration())
            return;
        if (!File.Exists(CopilotAuthPath))
            return;

        var auth = TryLoadAuth(CopilotAuthPath);
        if (auth is null)
            return;

        var channel = new StreamingCaptureChannel();
        var sessions = new InMemorySessionStore();
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["copilot-agent"]);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(BotNexus.Domain.Primitives.AgentId.From("copilot-agent"), BotNexus.Domain.Primitives.SessionId.From("phase5-live"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveCopilotAgentHandle(auth.Value));

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([channel]);
        manager.Setup(m => m.Get("web")).Returns(channel);

        var activity = new RecordingActivityBroadcaster();
        await using var host = new GatewayHost(
            supervisor.Object,
            router.Object,
            sessions,
            activity,
            manager.Object,
            Mock.Of<ISessionCompactor>(),
                new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
            NullLogger<GatewayHost>.Instance);

        await host.DispatchAsync(new InboundMessage
        {
            ChannelType = ChannelKey.From("web"),
            SenderId = "phase5-tester",
            ConversationId = "phase5-live-conv",
            SessionId = BotNexus.Domain.Primitives.SessionId.From("phase5-live"),
            Content = "Reply with a short greeting."
        });

        if (ShouldSkipForLiveIssue(activity))
            return;

        channel.StreamDeltas.Should().NotBeEmpty();
        string.Concat(channel.StreamDeltas).Should().NotBeNullOrWhiteSpace();
    }

    private static bool ShouldRunLiveIntegration()
        => string.Equals(Environment.GetEnvironmentVariable("BOTNEXUS_RUN_COPILOT_INTEGRATION"), "1", StringComparison.Ordinal);

    private static bool ShouldSkipForLiveIssue(RecordingActivityBroadcaster activity)
    {
        var errors = activity.Activities
            .Where(item => item.Type == GatewayActivityType.Error)
            .Select(item => item.Message ?? string.Empty)
            .ToList();

        if (errors.Count == 0)
            return false;

        return errors.All(IsAuthOrConnectivityIssueMessage);
    }

    private static bool IsAuthOrConnectivityIssueMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized = message.ToLowerInvariant();
        return normalized.Contains("unauthorized", StringComparison.Ordinal) ||
               normalized.Contains("forbidden", StringComparison.Ordinal) ||
               normalized.Contains("401", StringComparison.Ordinal) ||
               normalized.Contains("403", StringComparison.Ordinal) ||
               normalized.Contains("authentication", StringComparison.Ordinal) ||
               normalized.Contains("timed out", StringComparison.Ordinal) ||
               normalized.Contains("connection", StringComparison.Ordinal) ||
               normalized.Contains("name resolution", StringComparison.Ordinal) ||
               normalized.Contains("host", StringComparison.Ordinal) ||
               normalized.Contains("ssl", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal);
    }

    private static CopilotAuth? TryLoadAuth(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("github-copilot", out var copilot))
            return null;

        var access = copilot.TryGetProperty("access", out var accessElement) ? accessElement.GetString() : null;
        var endpoint = copilot.TryGetProperty("endpoint", out var endpointElement) ? endpointElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(endpoint))
            return null;

        return new CopilotAuth(access, endpoint);
    }

    private readonly record struct CopilotAuth(string AccessToken, string Endpoint);

    private sealed class LiveCopilotAgentHandle(CopilotAuth auth) : IAgentHandle
    {
        private readonly HttpClient _client = new();

        public BotNexus.Domain.Primitives.AgentId AgentId => BotNexus.Domain.Primitives.AgentId.From("copilot-agent");
        public BotNexus.Domain.Primitives.SessionId SessionId => BotNexus.Domain.Primitives.SessionId.From("phase5-live");
        public bool IsRunning => false;

        public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Content = message });

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = "gpt-4o",
                stream = true,
                max_tokens = 128,
                messages = new[] { new { role = "user", content = message } }
            });

            var endpoint = auth.Endpoint.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.105.0");
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.35.0");
            request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    yield break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var data = line["data:".Length..].Trim();
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    yield break;

                using var delta = JsonDocument.Parse(data);
                if (!delta.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                if (!choices[0].TryGetProperty("delta", out var deltaNode) || !deltaNode.TryGetProperty("content", out var contentNode))
                    continue;

                var content = contentNode.GetString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ContentDelta,
                        ContentDelta = content
                    };
                }
            }
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SteerAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamingCaptureChannel : IChannelAdapter
    {
        public ChannelKey ChannelType => ChannelKey.From("web");
        public string DisplayName => "Phase5 Test Channel";
        public bool SupportsStreaming => true;
        public bool SupportsSteering => false;
        public bool SupportsFollowUp => false;
        public bool SupportsThinkingDisplay => false;
        public bool SupportsToolDisplay => false;
        public bool IsRunning => true;
        public List<string> StreamDeltas { get; } = [];

        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        {
            StreamDeltas.Add(delta);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public List<GatewayActivity> Activities { get; } = [];

        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }

    private sealed class GatewayApiHarness : IAsyncDisposable
    {
        private GatewayApiHarness(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }

        public static async Task<GatewayApiHarness> StartAsync(string apiKey)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });

            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IGatewayAuthHandler>(_ => new ApiKeyGatewayAuthHandler(apiKey, NullLogger<ApiKeyGatewayAuthHandler>.Instance));
            builder.Services.AddSingleton<IAgentSupervisor>(_ => Mock.Of<IAgentSupervisor>());
            builder.Services.AddSingleton<IAgentConfigurationWriter>(_ => new NoOpAgentConfigurationWriter());
            builder.Services.AddSingleton<IAgentRegistry>(_ =>
            {
                var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
                registry.Register(new AgentDescriptor
                {
                    AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
                    DisplayName = "Agent A",
                    ModelId = "gpt-4.1",
                    ApiProvider = "copilot",
                    IsolationStrategy = "in-process"
                });
                return registry;
            });
            builder.Services.AddControllers().AddApplicationPart(typeof(AgentsController).Assembly);

            var app = builder.Build();
            app.UseMiddleware<GatewayAuthMiddleware>();
            app.MapControllers();
            await app.StartAsync();

            var address = app.Urls.Single();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new GatewayApiHarness(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
