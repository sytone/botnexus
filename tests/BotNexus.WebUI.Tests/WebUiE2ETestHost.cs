using System.Collections.Concurrent;
using System.Diagnostics;
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
        var expectedDispatchCount = Supervisor.Dispatches.Count(d => d.Kind == DispatchKind.Send) + 1;
        await Assertions.Expect(Page.Locator("#chat-input")).ToBeEditableAsync(new() { Timeout = 15000 });
        await Page.FillAsync("#chat-input", text);
        await Page.ClickAsync("#btn-send");
        await WaitForInvocationCountAsync(expectedDispatchCount);
        return Supervisor.Dispatches.Where(d => d.Kind == DispatchKind.Send).ElementAt(expectedDispatchCount - 1).SessionId;
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
            if (Supervisor.Dispatches.Count(d => d.Kind == DispatchKind.Send) >= expectedCount)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} dispatches. Saw {Supervisor.Dispatches.Count(d => d.Kind == DispatchKind.Send)}.");
    }

    public Task WaitForProcessingBarAsync(int timeoutMs = 15000)
        => Assertions.Expect(Page.Locator("#processing-status")).ToBeVisibleAsync(new() { Timeout = timeoutMs });

    public Task WaitForProcessingBarHiddenAsync(int timeoutMs = 15000)
        => Assertions.Expect(Page.Locator("#processing-status")).ToBeHiddenAsync(new() { Timeout = timeoutMs });

    public Task WaitForAbortButtonVisibleAsync(int timeoutMs = 15000)
        => Assertions.Expect(Page.Locator("#btn-abort")).ToBeVisibleAsync(new() { Timeout = timeoutMs });

    public Task WaitForAbortButtonHiddenAsync(int timeoutMs = 15000)
        => Assertions.Expect(Page.Locator("#btn-abort")).ToBeHiddenAsync(new() { Timeout = timeoutMs });

    public async Task ClickAbortAsync()
    {
        await WaitForAbortButtonVisibleAsync();
        await Page.ClickAsync("#btn-abort");
    }

    public Task<int> GetChatMessageCountAsync()
        => Page.Locator("#chat-messages .message").CountAsync();

    public async Task WaitForSystemMessageAsync(string text, int timeoutMs = 15000)
    {
        await Assertions.Expect(Page.Locator("#chat-messages .message.system-msg")).ToContainTextAsync(
            text,
            new() { Timeout = timeoutMs });
    }

    public async Task WaitForStreamingCompleteAsync(int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var processingVisible = await IsElementVisibleAsync("#processing-status:not(.hidden)");
            var abortVisible = await IsElementVisibleAsync("#btn-abort:not(.hidden)");
            var hasStreamingMessage = await IsElementVisibleAsync("#chat-messages .message.assistant.streaming");
            if (!processingVisible && !abortVisible && !hasStreamingMessage)
                return;

            await Task.Delay(75);
        }

        throw new TimeoutException("Timed out waiting for streaming to complete.");
    }

    public Task PressEscapeAsync()
        => Page.Keyboard.PressAsync("Escape");

    public async Task<bool> IsElementVisibleAsync(string selector)
    {
        try
        {
            return await Page.Locator(selector).First.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
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

internal static class DispatchKind
{
    public const string Send = "Send";
    public const string Steer = "Steer";
    public const string FollowUp = "FollowUp";
    public const string Abort = "Abort";
}

internal sealed record DispatchRecord(string AgentId, string SessionId, string Content, string Kind = DispatchKind.Send);

internal sealed record StreamToolCall(
    string ToolCallId,
    string ToolName,
    IReadOnlyDictionary<string, object?>? ToolArgs = null,
    string? ToolResult = null,
    bool ToolIsError = false,
    int DelayBeforeStartMs = 0,
    int DelayBeforeEndMs = 0);

internal sealed class RecordingStreamPlan
{
    public int InitialDelayMs { get; set; } = 120;
    public int DelayBetweenDeltasMs { get; set; } = 0;
    public string? ThinkingDelta { get; set; }
    public int ThinkingDelayMs { get; set; }
    public List<string> ContentDeltas { get; } = [];
    public List<StreamToolCall> ToolCalls { get; } = [];
    public bool EmitError { get; set; }
    public string ErrorMessage { get; set; } = "Injected stream error";
    public int ErrorDelayMs { get; set; }
    public bool CompleteAfterError { get; set; }
    public AgentResponseUsage? Usage { get; set; }
    public string? MessageId { get; set; }

    public static RecordingStreamPlan Default(string agentId, string message)
    {
        var plan = new RecordingStreamPlan();
        var delay = message.Contains("delayed", StringComparison.OrdinalIgnoreCase) ? 900 : 120;
        plan.InitialDelayMs = delay;
        plan.ContentDeltas.Add($"echo:{agentId}:{message}");
        return plan;
    }
}

internal sealed class RecordingAgentSupervisor : IAgentSupervisor
{
    private readonly ConcurrentDictionary<(string AgentId, string SessionId), RecordingAgentHandle> _handles = new();
    private readonly ConcurrentQueue<DispatchRecord> _dispatches = new();
    private readonly ConcurrentDictionary<(string AgentId, string SessionId), AgentInstanceStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RecordingStreamPlan>> _agentPlans = new();
    private readonly ConcurrentDictionary<(string AgentId, string SessionId), ConcurrentQueue<RecordingStreamPlan>> _sessionPlans = new();

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

    public void RecordDispatch(string agentId, string sessionId, string content, string kind = DispatchKind.Send)
        => _dispatches.Enqueue(new DispatchRecord(agentId, sessionId, content, kind));

    public void EnqueueAgentStreamPlan(string agentId, RecordingStreamPlan plan)
        => _agentPlans.GetOrAdd(agentId, _ => new ConcurrentQueue<RecordingStreamPlan>()).Enqueue(plan);

    public void EnqueueSessionStreamPlan(string agentId, string sessionId, RecordingStreamPlan plan)
        => _sessionPlans.GetOrAdd((agentId, sessionId), _ => new ConcurrentQueue<RecordingStreamPlan>()).Enqueue(plan);

    public RecordingStreamPlan GetStreamPlan(string agentId, string sessionId, string message)
    {
        if (_sessionPlans.TryGetValue((agentId, sessionId), out var sessionQueue) &&
            sessionQueue.TryDequeue(out var sessionPlan))
        {
            return sessionPlan;
        }

        if (_agentPlans.TryGetValue(agentId, out var agentQueue) &&
            agentQueue.TryDequeue(out var agentPlan))
        {
            return agentPlan;
        }

        return RecordingStreamPlan.Default(agentId, message);
    }

    public void SetStatus(string agentId, string sessionId, AgentInstanceStatus status)
        => _statuses[(agentId, sessionId)] = status;
}

internal sealed class RecordingAgentHandle(string agentId, string sessionId, RecordingAgentSupervisor supervisor) : IAgentHandle
{
    private readonly ConcurrentQueue<string> _followUps = new();
    private volatile bool _abortRequested;

    public string AgentId { get; } = agentId;
    public string SessionId { get; } = sessionId;
    public bool IsRunning { get; private set; }

    public Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse { Content = $"echo:{AgentId}:{message}" });

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        supervisor.RecordDispatch(AgentId, SessionId, message, DispatchKind.Send);
        IsRunning = true;
        _abortRequested = false;
        supervisor.SetStatus(AgentId, SessionId, AgentInstanceStatus.Running);

        AgentResponseUsage? finalUsage = null;
        try
        {
            var messages = new Queue<string>();
            messages.Enqueue(message);

            while (!_abortRequested && messages.Count > 0)
            {
                var current = messages.Dequeue();
                var plan = supervisor.GetStreamPlan(AgentId, SessionId, current);
                finalUsage = plan.Usage ?? finalUsage;
                var messageId = plan.MessageId ?? $"{AgentId}-{Guid.NewGuid():N}";

                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageStart,
                    SessionId = SessionId,
                    MessageId = messageId
                };

                if (!await DelayWithAbortAsync(plan.InitialDelayMs, cancellationToken))
                    break;

                if (!string.IsNullOrWhiteSpace(plan.ThinkingDelta))
                {
                    if (!await DelayWithAbortAsync(plan.ThinkingDelayMs, cancellationToken))
                        break;

                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ThinkingDelta,
                        SessionId = SessionId,
                        MessageId = messageId,
                        ThinkingContent = plan.ThinkingDelta
                    };
                }

                foreach (var tool in plan.ToolCalls)
                {
                    if (!await DelayWithAbortAsync(tool.DelayBeforeStartMs, cancellationToken))
                        break;

                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolStart,
                        SessionId = SessionId,
                        MessageId = messageId,
                        ToolCallId = tool.ToolCallId,
                        ToolName = tool.ToolName,
                        ToolArgs = tool.ToolArgs
                    };

                    if (!await DelayWithAbortAsync(tool.DelayBeforeEndMs, cancellationToken))
                        break;

                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolEnd,
                        SessionId = SessionId,
                        MessageId = messageId,
                        ToolCallId = tool.ToolCallId,
                        ToolName = tool.ToolName,
                        ToolResult = tool.ToolResult ?? "ok",
                        ToolIsError = tool.ToolIsError
                    };
                }

                if (_abortRequested)
                    break;

                var deltas = plan.ContentDeltas.Count == 0
                    ? [$"echo:{AgentId}:{current}"]
                    : plan.ContentDeltas;

                for (var i = 0; i < deltas.Count; i++)
                {
                    if (i > 0 && !await DelayWithAbortAsync(plan.DelayBetweenDeltasMs, cancellationToken))
                        break;

                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ContentDelta,
                        SessionId = SessionId,
                        MessageId = messageId,
                        ContentDelta = deltas[i]
                    };
                }

                if (_abortRequested)
                    break;

                if (plan.EmitError)
                {
                    if (!await DelayWithAbortAsync(plan.ErrorDelayMs, cancellationToken))
                        break;

                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Error,
                        SessionId = SessionId,
                        MessageId = messageId,
                        ErrorMessage = plan.ErrorMessage
                    };

                    if (!plan.CompleteAfterError)
                        yield break;
                }

                while (_followUps.TryDequeue(out var followUp))
                    messages.Enqueue(followUp);
            }

            if (!_abortRequested)
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.MessageEnd,
                    SessionId = SessionId,
                    Usage = finalUsage
                };
            }
        }
        finally
        {
            IsRunning = false;
            supervisor.SetStatus(AgentId, SessionId, _abortRequested ? AgentInstanceStatus.Stopped : AgentInstanceStatus.Idle);
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        supervisor.RecordDispatch(AgentId, SessionId, string.Empty, DispatchKind.Abort);
        _abortRequested = true;
        IsRunning = false;
        supervisor.SetStatus(AgentId, SessionId, AgentInstanceStatus.Stopped);
        return Task.CompletedTask;
    }

    public Task SteerAsync(string message, CancellationToken cancellationToken = default)
    {
        supervisor.RecordDispatch(AgentId, SessionId, message, DispatchKind.Steer);
        return Task.CompletedTask;
    }

    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default)
    {
        supervisor.RecordDispatch(AgentId, SessionId, message, DispatchKind.FollowUp);
        _followUps.Enqueue(message);
        return Task.CompletedTask;
    }

    private async Task<bool> DelayWithAbortAsync(int delayMs, CancellationToken cancellationToken)
    {
        if (_abortRequested)
            return false;

        if (delayMs > 0)
            await Task.Delay(delayMs, cancellationToken);

        return !_abortRequested;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
