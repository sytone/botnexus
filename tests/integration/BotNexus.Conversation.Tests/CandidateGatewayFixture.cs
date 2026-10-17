using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.SignalR;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Conversation.Tests;

[CollectionDefinition("CandidateGateway", DisableParallelization = true)]
public sealed class CandidateGatewayCollection : ICollectionFixture<CandidateGatewayFixture>;

/// <summary>
/// Candidate Program, HTTP middleware/controllers, hub, router, and production in-memory stores.
/// No TCP listener, developer configuration, provider calls, cron worker, or child process.
/// Startup exceptions fail the collection rather than converting coverage to skips.
/// </summary>
public sealed class CandidateGatewayFixture : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "botnexus-conversation-candidate", Guid.NewGuid().ToString("N"));
    private readonly List<(string Name, string? Value)> _environment = [];
    private TestLogger? _logger;
    private int _agentNumber;
    public WebApplicationFactory<Program> Factory { get; private set; } = new();
    public HttpClient Http { get; private set; } = new();
    public ConversationApiClient Conversations => new(Http);
    private TestSignalRClient? _signalR;
    public TestSignalRClient SignalR => _signalR ?? throw new InvalidOperationException("Candidate hub is not initialized.");
    public string SeedConversationId { get; private set; } = string.Empty;
    public const string SeedSessionId = "candidate-seed-session";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        // Program resolves bootstrap logging/home before DI. Scope these overrides to this test
        // process, restore them after disposal, and serialize all hosts in this assembly.
        foreach (var (name, value) in new[] { ("BOTNEXUS_HOME", _home), ("BOTNEXUS_DATA_DIR", _home) })
        {
            _environment.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
        var configPath = Path.Combine(_home, "config.json");
        await File.WriteAllTextAsync(configPath, "{}");
        Factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("BotNexus:ConfigPath", configPath);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IIsolationStrategy>();
                // No discovery HTTP or subprocess/sandbox strategy can be constructed.
                services.RemoveAll<LlmClient>();
                services.AddSingleton(new LlmClient(new ApiProviderRegistry(), new ModelRegistry()));
                services.Configure<SessionWarmupOptions>(options => options.CollapseChannelContinuations = false);
                services.RemoveAll<ISessionStore>();
                services.AddSingleton<ISessionStore>(sp => new InMemorySessionStore(null, sp.GetRequiredService<IConversationStore>()));
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore, InMemoryConversationStore>();
                services.RemoveAll<IAgentRegistry>();
                services.AddSingleton<IAgentRegistry, DefaultAgentRegistry>();
                // Register the production extension explicitly: its background loader is disabled.
                new SignalRServiceContributor().ConfigureServices(services);
                services.AddSingleton<SignalRChannelAdapter>();
                services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<SignalRChannelAdapter>());
                services.AddSingleton<IEndpointContributor, SignalREndpointContributor>();
                services.RemoveAll<IInboundMessageOrchestrator>();
                services.AddSingleton<IInboundMessageOrchestrator, DeterministicAgentExecution>();
            });
        });
        Http.Dispose();
        Http = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://candidate.test") });
        RegisterAgent("assistant");
        var router = Factory.Services.GetRequiredService<IConversationRouter>();
        var resolution = await router.ResolveInboundAsync(AgentId.From("assistant"), ChannelKey.From("signalr"), ChannelAddress.From("assistant"));
        SeedConversationId = resolution.Conversation.ConversationId.Value;
        var sessions = Factory.Services.GetRequiredService<ISessionStore>();
        // Replace the router-created empty session with the fixed seed identity, so history
        // has exactly one segment and future assistant sends reuse this same seeded session.
        await sessions.ArchiveAsync(resolution.SessionId);
        resolution.Conversation.ActiveSessionId = SessionId.From(SeedSessionId);
        await Factory.Services.GetRequiredService<IConversationStore>().SaveAsync(resolution.Conversation);
        var session = new GatewaySession
        {
            SessionId = SessionId.From(SeedSessionId), AgentId = AgentId.From("assistant"),
            ConversationId = resolution.Conversation.ConversationId, ChannelType = ChannelKey.From("signalr"),
            History = [
                new() { Role = MessageRole.User, Content = "seed question", Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
                new() { Role = MessageRole.Tool, Content = "seed tool failed", ToolName = "seed_tool", ToolCallId = "seed-call", ToolArgs = "{\"value\":42}", ToolIsError = true, Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:01Z") },
                new() { Role = MessageRole.Assistant, Content = "seed answer", Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:02Z") }
            ]
        };
        await Factory.Services.GetRequiredService<ISessionStore>().SaveAsync(session);
        _logger = new TestLogger(Path.Combine(_home, "client-logs"));
        _signalR = new TestSignalRClient(Factory.Server.CreateHandler(), _logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SignalR.ConnectAsync(timeout.Token);
    }

    public string NewAgent()
    {
        var id = $"candidate-agent-{Interlocked.Increment(ref _agentNumber)}";
        RegisterAgent(id);
        return id;
    }

    private void RegisterAgent(string id) => Factory.Services.GetRequiredService<IAgentRegistry>().Register(new AgentDescriptor
    {
        AgentId = AgentId.From(id), DisplayName = id, ModelId = "candidate-model", ApiProvider = "candidate-provider", IsolationStrategy = "in-process"
    });

    public async Task DisposeAsync()
    {
        try
        {
            if (_signalR is not null) await _signalR.DisposeAsync();
            Http.Dispose();
            await Factory.DisposeAsync();
            // Program preserves its static bootstrap logger separately from host logging.
            // Close it before removing the disposable home, including on Windows.
            await Serilog.Log.CloseAndFlushAsync();
            _logger?.Dispose();
        }
        finally
        {
            foreach (var (name, value) in _environment) Environment.SetEnvironmentVariable(name, value);
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
    }

    /// <summary>Agent execution is outside the transport/store seam. Routing and persistence
    /// have already run through the real dispatcher; emit a deterministic provider-start event
    /// through the REAL SignalR adapter, never by injecting a client-side event.</summary>
    private sealed class DeterministicAgentExecution(ISessionStore sessions, SignalRChannelAdapter adapter) : IInboundMessageOrchestrator
    {
        public async Task<InboundDispatchStatus> PostAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            var hints = InboundMessageRoutingHints.FromMessage(message);
            var id = hints.RequestedSessionId ?? throw new InvalidOperationException("Hub must resolve a session before execution.");
            var session = await sessions.GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("Resolved session missing.");
            await adapter.SendStreamEventAsync(new ChannelStreamTarget(session.ConversationId, id, ChannelAddress.From(session.AgentId.Value), null),
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, SessionId = id, ConversationId = session.ConversationId, AgentId = session.AgentId }, cancellationToken);
            return InboundDispatchStatus.Accepted;
        }
        public bool Post(InboundMessage message) => throw new NotSupportedException("Use awaited admission in this fixture.");
        public Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Only hub admission is in scope.");
    }
}
