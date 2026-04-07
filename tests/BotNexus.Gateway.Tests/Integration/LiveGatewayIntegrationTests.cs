using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class LiveGatewayIntegrationTests
{
    private static readonly string AuthPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".botnexus-agent", "auth.json");

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    public async Task GatewayStartupTest_HealthEndpoint_ReturnsOk()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    public async Task GatewayStartupTest_SwaggerEndpoint_ReturnsOk()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    public async Task RestApiTests_AgentsSessionsAndConfigEndpoints_ReturnExpectedResponses()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var listAgentsResponse = await client.GetAsync("/api/agents");
        listAgentsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listSessionsResponse = await client.GetAsync("/api/sessions");
        listSessionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var descriptor = new AgentDescriptor
        {
            AgentId = $"integration-agent-{Guid.NewGuid():N}",
            DisplayName = "Integration Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/agents", descriptor);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var validateResponse = await client.GetAsync("/api/config/validate");
        validateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    public async Task WebSocketConnectionTest_WsEndpoint_SendsConnectedMessage()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = "ws-agent",
            DisplayName = "WebSocket Agent",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/agents", descriptor);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws?agent=ws-agent&session=ws-session"), cts.Token);

        // Collect all messages within timeout — handler may send connected + session_switched
        var messages = new List<JsonDocument>();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var payload = await ReceiveTextAsync(socket, cts.Token);
                messages.Add(JsonDocument.Parse(payload));
            }
        }
        catch (OperationCanceledException) { }

        messages.Should().Contain(doc =>
            doc.RootElement.GetProperty("type").GetString() == "connected");
    }

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    public async Task ActivityWebSocketTest_ActivitySubscription_StreamsPublishedEvents()
    {
        await using var factory = CreateFactory();
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws/activity"), CancellationToken.None);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var broadcaster = scope.ServiceProvider.GetRequiredService<IActivityBroadcaster>();
            await broadcaster.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.System,
                AgentId = "activity-agent",
                Message = "activity-event"
            });
        }

        var payload = await ReceiveTextAsync(socket, CancellationToken.None);
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("agentId").GetString().Should().Be("activity-agent");
        doc.RootElement.GetProperty("message").GetString().Should().Be("activity-event");
    }

    [Fact(Skip = "Live WebSocket tests hang in CI — TestServer WebSocket lifecycle issue")]
    [Trait("Category", "Live")]
    public async Task LiveChatTest_CopilotBackedAgent_StreamsResponse()
    {
        if (!ShouldRunLiveIntegration())
            return;

        var auth = TryLoadAuth();
        if (auth is null)
            return;

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync("copilot-live", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveCopilotAgentHandle(auth.Value));

        await using var factory = CreateFactory(services =>
        {
            services.RemoveAll<IAgentSupervisor>();
            services.AddSingleton(supervisor.Object);
        });

        using var client = factory.CreateClient();
        var descriptor = new AgentDescriptor
        {
            AgentId = "copilot-live",
            DisplayName = "Copilot Live Agent",
            ModelId = "gpt-4o",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/agents", descriptor);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws?agent=copilot-live&session=live-session"), CancellationToken.None);
        await ReceiveTextAsync(socket, CancellationToken.None);
        await ReceiveTextAsync(socket, CancellationToken.None);

        await SendTextAsync(socket, """{"type":"message","content":"Reply with a short greeting."}""", CancellationToken.None);
        var payload = await ReceiveUntilMessageEndAsync(socket, CancellationToken.None);
        payload.Should().Contain("content_delta");
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection>? configureTestServices = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                {
                    configureTestServices?.Invoke(services);
                });
            });

    private static bool ShouldRunLiveIntegration()
        => string.Equals(Environment.GetEnvironmentVariable("BOTNEXUS_RUN_COPILOT_INTEGRATION"), "1", StringComparison.Ordinal);

    private static CopilotAuth? TryLoadAuth()
    {
        if (!File.Exists(AuthPath))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(AuthPath));
        if (!document.RootElement.TryGetProperty("github-copilot", out var copilot))
            return null;

        var access = copilot.TryGetProperty("access", out var accessElement) ? accessElement.GetString() : null;
        var endpoint = copilot.TryGetProperty("endpoint", out var endpointElement) ? endpointElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(endpoint))
            return null;

        return new CopilotAuth(access, endpoint);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        result.MessageType.Should().Be(WebSocketMessageType.Text);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task SendTextAsync(WebSocket socket, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveUntilMessageEndAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var payloads = new List<string>();

        while (!linked.IsCancellationRequested)
        {
            var payload = await ReceiveTextAsync(socket, linked.Token);
            payloads.Add(payload);
            if (payload.Contains("\"type\":\"message_end\"", StringComparison.Ordinal))
                break;
        }

        return string.Join('\n', payloads);
    }

    private readonly record struct CopilotAuth(string AccessToken, string Endpoint);

    private sealed class LiveCopilotAgentHandle(CopilotAuth auth) : IAgentHandle
    {
        private readonly HttpClient _client = new();

        public string AgentId => "copilot-live";
        public string SessionId => "live-session";
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
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
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

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

