using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

[CollectionDefinition("Playwright", DisableParallelization = true)]
public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private KestrelWebApplicationFactory<Program>? _factory;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private ResettableInMemorySessionStore? _sessionStore;

    public HttpClient ApiClient { get; private set; } = default!;
    public string BaseUrl { get; private set; } = string.Empty;
    internal RecordingAgentSupervisor Supervisor { get; private set; } = default!;
    internal TestSubAgentManager SubAgentManager { get; private set; } = default!;

    public async Task InitializeAsync()
        => await InitializeInfrastructureAsync();

    internal async Task<WebUiE2ETestHost> CreatePageAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
                await RestartInfrastructureAsync();

            try
            {
                return await CreatePageCoreAsync();
            }
            catch (Exception) when (attempt == 0)
            {
            }
        }

        return await CreatePageCoreAsync();
    }

    private async Task<WebUiE2ETestHost> CreatePageCoreAsync()
    {
        if (_browser is null)
            throw new InvalidOperationException("Playwright fixture was not initialized.");

        Supervisor.Reset();
        SubAgentManager.Reset();
        _sessionStore?.ResetAndSeed(AgentA, AgentB);

        var browserContext = await _browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        var host = new WebUiE2ETestHost(Supervisor, SubAgentManager, ApiClient, BaseUrl, browserContext, page);

        try
        {
            await page.GotoAsync(BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator("#connection-status.connected").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
            await host.WaitForAgentEntryAsync(AgentA);
            await host.WaitForAgentEntryAsync(AgentB);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await DisposeInfrastructureAsync();
    }

    private async Task RestartInfrastructureAsync()
    {
        await DisposeInfrastructureAsync();
        await InitializeInfrastructureAsync();
    }

    private async Task InitializeInfrastructureAsync()
    {
        var supervisor = new RecordingAgentSupervisor();
        var subAgentManager = new TestSubAgentManager();
        var sessionStore = new ResettableInMemorySessionStore();
        var factory = CreateFactory(supervisor, subAgentManager, sessionStore);
        var apiClient = factory.CreateKestrelClient();
        var baseUrl = factory.RootUri.TrimEnd('/');

        await RegisterAgentAsync(apiClient, AgentA);
        await RegisterAgentAsync(apiClient, AgentB);

        IPlaywright playwright;
        IBrowser browser;
        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException($"Playwright browsers not installed. Run: pwsh bin/Debug/net10.0/playwright.ps1 install chromium. {ex.Message}", ex);
        }

        _factory = factory;
        _playwright = playwright;
        _browser = browser;
        _sessionStore = sessionStore;
        ApiClient = apiClient;
        BaseUrl = baseUrl;
        Supervisor = supervisor;
        SubAgentManager = subAgentManager;
    }

    private async Task DisposeInfrastructureAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _browser = null;

        _playwright?.Dispose();
        _playwright = null;

        if (!ReferenceEquals(ApiClient, null))
            ApiClient.Dispose();

        if (_factory is not null)
            await _factory.DisposeAsync();
        _factory = null;
    }

    private static KestrelWebApplicationFactory<Program> CreateFactory(
        RecordingAgentSupervisor supervisor,
        TestSubAgentManager subAgentManager,
        ResettableInMemorySessionStore sessionStore)
    {
        var rootUri = GetEphemeralLoopbackUrl();
        return new KestrelWebApplicationFactory<Program>(rootUri, builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var descriptor in hostedServices)
                    services.Remove(descriptor);

                services.RemoveAll<IAgentConfigurationWriter>();
                services.AddSingleton<IAgentConfigurationWriter, NoOpAgentConfigurationWriter>();

                services.RemoveAll<IAgentSupervisor>();
                services.AddSingleton<IAgentSupervisor>(supervisor);

                services.RemoveAll<ISessionStore>();
                services.AddSingleton<ISessionStore>(sessionStore);

                services.RemoveAll<ISubAgentManager>();
                services.AddSingleton<ISubAgentManager>(subAgentManager);
            });
        });
    }

    private static string GetEphemeralLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static async Task RegisterAgentAsync(HttpClient client, string agentId)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = agentId,
            DisplayName = $"Test Agent {agentId}",
            ModelId = "gpt-4.1",
            ApiProvider = "copilot",
            IsolationStrategy = "in-process"
        };

        var response = await client.PostAsJsonAsync("/api/agents", descriptor);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }
}

internal sealed class ResettableInMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, GatewaySession> _sessions = [];
    private readonly Lock _sync = new();

    public Task<GatewaySession?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return Task.FromResult(_sessions.GetValueOrDefault(sessionId));
    }

    public Task<GatewaySession> GetOrCreateAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
                return Task.FromResult(existing);

            var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
            _sessions[sessionId] = session;
            return Task.FromResult(session);
        }
    }

    public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GatewaySession>> ListAsync(string? agentId = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            IReadOnlyList<GatewaySession> result = agentId is null
                ? [.. _sessions.Values]
                : _sessions.Values.Where(s => s.AgentId == agentId).ToList();
            return Task.FromResult(result);
        }
    }

    public void Reset()
    {
        lock (_sync)
            _sessions.Clear();
    }

    public void ResetAndSeed(params string[] agentIds)
    {
        lock (_sync)
        {
            _sessions.Clear();
            foreach (var agentId in agentIds)
            {
                var sessionId = $"{agentId}-{Guid.NewGuid():N}";
                _sessions[sessionId] = new GatewaySession
                {
                    SessionId = sessionId,
                    AgentId = agentId
                };
            }
        }
    }
}
