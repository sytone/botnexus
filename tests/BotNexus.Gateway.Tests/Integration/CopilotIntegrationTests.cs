using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class CopilotIntegrationTests
{
    private static readonly string AuthPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".botnexus-agent", "auth.json");

    [Fact]
    public async Task DispatchAsync_WithCopilotPrompt_ReturnsResponse()
    {
        if (!ShouldRunIntegration())
            return;

        var auth = TryLoadAuth();
        if (auth is null)
            return;

        var channel = new RecordingChannelAdapter(supportsStreaming: false);
        var host = CreateHost(new CopilotAgentHandle(auth), channel);

        try
        {
            await host.DispatchAsync(CreateMessage("Reply with one short sentence."));
        }
        catch (Exception ex) when (IsAuthOrConnectivityIssue(ex))
        {
            return;
        }

        channel.SentMessages.Should().ContainSingle();
        channel.SentMessages.Single().Content.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DispatchAsync_WithCopilotStreaming_EmitsDeltas()
    {
        if (!ShouldRunIntegration())
            return;

        var auth = TryLoadAuth();
        if (auth is null)
            return;

        var channel = new RecordingChannelAdapter(supportsStreaming: true);
        var host = CreateHost(new CopilotAgentHandle(auth), channel);

        try
        {
            await host.DispatchAsync(CreateMessage("Answer with a short greeting."));
        }
        catch (Exception ex) when (IsAuthOrConnectivityIssue(ex))
        {
            return;
        }

        channel.StreamDeltas.Should().NotBeEmpty();
        string.Concat(channel.StreamDeltas).Should().NotBeNullOrWhiteSpace();
    }

    private static GatewayHost CreateHost(IAgentHandle handle, IChannelAdapter channel)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["copilot-agent"]);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync("copilot-agent", "integration-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        var channels = new Mock<IChannelManager>();
        channels.SetupGet(c => c.Adapters).Returns([channel]);
        channels.Setup(c => c.Get("web")).Returns(channel);
        return new GatewayHost(
            supervisor.Object,
            router.Object,
            new InMemorySessionStore(),
            new NullActivityBroadcaster(),
            channels.Object,
            NullLogger<GatewayHost>.Instance);
    }

    private static InboundMessage CreateMessage(string content)
        => new()
        {
            ChannelType = "web",
            SenderId = "integration-user",
            ConversationId = "copilot-integration-conversation",
            Content = content,
            SessionId = "integration-session"
        };

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

    private static bool ShouldRunIntegration()
        => string.Equals(Environment.GetEnvironmentVariable("BOTNEXUS_RUN_COPILOT_INTEGRATION"), "1", StringComparison.Ordinal);

    private static bool IsAuthOrConnectivityIssue(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or JsonException;

    private sealed record CopilotAuth(string AccessToken, string Endpoint);

    private sealed class CopilotAgentHandle(CopilotAuth auth) : IAgentHandle
    {
        private readonly HttpClient _httpClient = new();

        public string AgentId => "copilot-agent";
        public string SessionId => "integration-session";
        public bool IsRunning => false;

        public async Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = "gpt-4o",
                stream = false,
                max_tokens = 96,
                messages = new[]
                {
                    new { role = "user", content = message }
                }
            });

            using var request = CreateRequest(payload);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return new AgentResponse { Content = content };
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var payload = JsonSerializer.Serialize(new
            {
                model = "gpt-4o",
                stream = true,
                max_tokens = 96,
                messages = new[]
                {
                    new { role = "user", content = message }
                }
            });

            using var request = CreateRequest(payload);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                var payloadLine = line["data:".Length..].Trim();
                if (string.Equals(payloadLine, "[DONE]", StringComparison.Ordinal))
                    yield break;

                using var doc = JsonDocument.Parse(payloadLine);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                if (choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.GetString();
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
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SteerAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            return ValueTask.CompletedTask;
        }

        private HttpRequestMessage CreateRequest(string payload)
        {
            var endpoint = auth.Endpoint.TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.105.0");
            request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.35.0");
            request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
            return request;
        }
    }

    private sealed class RecordingChannelAdapter(bool supportsStreaming) : IChannelAdapter
    {
        public string ChannelType => "web";
        public string DisplayName => "Integration Channel";
        public bool SupportsStreaming => supportsStreaming;
        public bool IsRunning => true;

        public List<OutboundMessage> SentMessages { get; } = [];
        public List<string> StreamDeltas { get; } = [];

        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default)
        {
            StreamDeltas.Add(delta);
            return Task.CompletedTask;
        }
    }

    private sealed class NullActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);
            yield break;
        }
    }
}
