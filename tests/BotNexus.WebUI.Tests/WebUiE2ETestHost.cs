using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace BotNexus.WebUI.Tests;

internal sealed class WebUiE2ETestHost : IAsyncDisposable
{
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private readonly RecordingAgentSupervisor _supervisor;
    private KestrelWebApplicationFactory<Program>? _factory;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private WebUiE2ETestHost(RecordingAgentSupervisor supervisor)
    {
        _supervisor = supervisor;
    }

    public required HttpClient ApiClient { get; init; }
    public required IPage Page { get; init; }
    public required string BaseUrl { get; init; }
    public RecordingAgentSupervisor Supervisor => _supervisor;

    public static async Task<WebUiE2ETestHost> StartAsync()
    {
        var supervisor = new RecordingAgentSupervisor();
        var factory = CreateFactory(supervisor);
        var apiClient = factory.CreateKestrelClient();

        await RegisterAgentAsync(apiClient, AgentA);
        await RegisterAgentAsync(apiClient, AgentB);

        var baseUrl = factory.RootUri.TrimEnd('/');

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

        var page = await browser.NewPageAsync();
        await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("#connection-status.connected").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        var host = new WebUiE2ETestHost(supervisor)
        {
            ApiClient = apiClient,
            Page = page,
            BaseUrl = baseUrl,
            _factory = factory,
            _playwright = playwright,
            _browser = browser
        };

        await host.WaitForAgentEntryAsync(AgentA);
        await host.WaitForAgentEntryAsync(AgentB);
        return host;
    }

    public async Task WaitForAgentEntryAsync(string agentId)
    {
        await Page.Locator($"#sessions-list .list-item[data-agent-id='{agentId}']").First.WaitForAsync(
            new LocatorWaitForOptions { Timeout = 15000 });
    }

    public async Task OpenAgentTimelineAsync(string agentId)
    {
        await WaitForAgentEntryAsync(agentId);
        var agentEntry = Page.Locator($"#sessions-list .list-item[data-agent-id='{agentId}'][data-channel-type='web chat']").First;
        var selectedSessionId = await agentEntry.GetAttributeAsync("data-session-id");
        await agentEntry.ClickAsync();
        await Assertions.Expect(Page.Locator("#chat-title")).ToContainTextAsync(agentId, new() { Timeout = 15000 });
        await Assertions.Expect(Page.Locator("#chat-input")).ToBeEditableAsync(new() { Timeout = 15000 });
        if (!string.IsNullOrWhiteSpace(selectedSessionId))
            await WaitForCurrentSessionIdAsync(selectedSessionId);
    }

    public async Task<string> SendMessageAsync(string text)
    {
        var expectedDispatchCount = Supervisor.Dispatches.Count + 1;
        await Assertions.Expect(Page.Locator("#chat-input")).ToBeEditableAsync(new() { Timeout = 15000 });
        await Page.FillAsync("#chat-input", text);
        await Page.ClickAsync("#btn-send");
        await WaitForInvocationCountAsync(expectedDispatchCount);
        return Supervisor.Dispatches[expectedDispatchCount - 1].SessionId;
    }

    public async Task<string> WaitForCurrentSessionIdAsync(string? expectedSessionId = null, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            var sessionId = await Page.GetAttributeAsync("#session-id-text", "title");
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                (string.IsNullOrWhiteSpace(expectedSessionId) || string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal)))
                return sessionId;

            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for current session id.");
    }

    public async Task WaitForInvocationCountAsync(int expectedCount, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (Supervisor.Dispatches.Count >= expectedCount)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} dispatches. Saw {Supervisor.Dispatches.Count}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();
        ApiClient.Dispose();

        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private static KestrelWebApplicationFactory<Program> CreateFactory(RecordingAgentSupervisor supervisor)
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
                services.AddSingleton<ISessionStore, InMemorySessionStore>();
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

internal sealed class KestrelWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
{
    private readonly Action<IWebHostBuilder>? _configure;
    private IHost? _kestrelHost;
    public string RootUri { get; }

    public KestrelWebApplicationFactory(string rootUri, Action<IWebHostBuilder>? configure = null)
    {
        RootUri = rootUri;
        _configure = configure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _configure?.Invoke(builder);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var testHost = builder.Build();

        builder.ConfigureWebHost(webHostBuilder =>
        {
            webHostBuilder.UseKestrel();
            webHostBuilder.UseUrls(RootUri);
        });

        _kestrelHost = builder.Build();
        _kestrelHost.Start();
        testHost.Start();

        return testHost;
    }

    public HttpClient CreateKestrelClient()
    {
        _ = Server;
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri(RootUri)
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.Dispose();
            _kestrelHost = null;
        }

        base.Dispose(disposing);
    }
}

internal sealed record DispatchRecord(string AgentId, string SessionId, string Content);

internal sealed class RecordingAgentSupervisor : IAgentSupervisor
{
    private readonly ConcurrentDictionary<(string AgentId, string SessionId), RecordingAgentHandle> _handles = new();
    private readonly ConcurrentQueue<DispatchRecord> _dispatches = new();
    private readonly ConcurrentDictionary<(string AgentId, string SessionId), AgentInstanceStatus> _statuses = new();

    public IReadOnlyList<DispatchRecord> Dispatches => _dispatches.ToList();

    public Task<IAgentHandle> GetOrCreateAsync(string agentId, string sessionId, CancellationToken cancellationToken = default)
    {
        var handle = _handles.GetOrAdd((agentId, sessionId), key => new RecordingAgentHandle(key.AgentId, key.SessionId, this));
        return Task.FromResult<IAgentHandle>(handle);
    }

    public Task StopAsync(string agentId, string sessionId, CancellationToken cancellationToken = default)
    {
        _statuses[(agentId, sessionId)] = AgentInstanceStatus.Stopped;
        return Task.CompletedTask;
    }

    public AgentInstance? GetInstance(string agentId, string sessionId)
    {
        if (!_handles.ContainsKey((agentId, sessionId)))
            return null;

        var status = _statuses.TryGetValue((agentId, sessionId), out var value)
            ? value
            : AgentInstanceStatus.Idle;

        return new AgentInstance
        {
            AgentId = agentId,
            SessionId = sessionId,
            InstanceId = $"{agentId}::{sessionId}",
            IsolationStrategy = "in-process",
            Status = status
        };
    }

    public IReadOnlyList<AgentInstance> GetAllInstances()
        => _handles.Keys
            .Select(key => GetInstance(key.AgentId, key.SessionId))
            .Where(instance => instance is not null)
            .Cast<AgentInstance>()
            .ToList();

    public Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        _handles.Clear();
        _statuses.Clear();
        return Task.CompletedTask;
    }

    public void RecordDispatch(string agentId, string sessionId, string content)
        => _dispatches.Enqueue(new DispatchRecord(agentId, sessionId, content));

    public void SetStatus(string agentId, string sessionId, AgentInstanceStatus status)
        => _statuses[(agentId, sessionId)] = status;
}

internal sealed class RecordingAgentHandle(string agentId, string sessionId, RecordingAgentSupervisor supervisor) : IAgentHandle
{
    public string AgentId { get; } = agentId;
    public string SessionId { get; } = sessionId;
    public bool IsRunning { get; private set; }

    public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse { Content = $"echo:{AgentId}:{message}" });

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        supervisor.RecordDispatch(AgentId, SessionId, message);
        IsRunning = true;
        supervisor.SetStatus(AgentId, SessionId, AgentInstanceStatus.Running);

        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.MessageStart,
            SessionId = SessionId
        };

        var delay = message.Contains("delayed", StringComparison.OrdinalIgnoreCase) ? 900 : 120;
        await Task.Delay(delay, cancellationToken);

        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.ContentDelta,
            SessionId = SessionId,
            ContentDelta = $"echo:{AgentId}:{message}"
        };

        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.MessageEnd,
            SessionId = SessionId
        };

        IsRunning = false;
        supervisor.SetStatus(AgentId, SessionId, AgentInstanceStatus.Idle);
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        supervisor.SetStatus(AgentId, SessionId, AgentInstanceStatus.Stopped);
        return Task.CompletedTask;
    }

    public Task SteerAsync(string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
