using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
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
    private readonly RecordingAgentSupervisor _supervisor;
    private readonly TestSubAgentManager _subAgentManager;
    private readonly IBrowserContext _browserContext;
    private readonly IPage _page;
    private readonly ConcurrentQueue<string> _consoleMessages = new();

    internal WebUiE2ETestHost(
        RecordingAgentSupervisor supervisor,
        TestSubAgentManager subAgentManager,
        HttpClient apiClient,
        string baseUrl,
        IBrowserContext browserContext,
        IPage page)
    {
        _supervisor = supervisor;
        _subAgentManager = subAgentManager;
        ApiClient = apiClient;
        BaseUrl = baseUrl;
        _browserContext = browserContext;
        _page = page;
        _page.Console += (_, message) => _consoleMessages.Enqueue(message.Text);
    }

    public HttpClient ApiClient { get; }
    public IPage Page => _page;
    public string BaseUrl { get; }
    public RecordingAgentSupervisor Supervisor => _supervisor;
    public TestSubAgentManager SubAgentManager => _subAgentManager;

    public async Task WaitForAgentEntryAsync(string agentId)
    {
        var locator = Page.Locator($"#sessions-list .list-item[data-agent-id='{agentId}']").First;
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        }
        catch (PlaywrightException)
        {
            if (await IsElementVisibleAsync("#btn-refresh-sessions"))
                await Page.ClickAsync("#btn-refresh-sessions");

            await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        }
    }

    public async Task OpenAgentTimelineAsync(string agentId)
    {
        await WaitForAgentEntryAsync(agentId);
        var agentEntry = Page.Locator($"#sessions-list .list-item[data-agent-id='{agentId}'][data-channel-type='web chat']").First;
        await agentEntry.ClickAsync();
        await Assertions.Expect(Page.Locator("#chat-input")).ToBeEditableAsync(new() { Timeout = 15000 });
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

    public int GetHubInvocationCount(string method)
    {
        var marker = $"[BotNexus:hub] → {method}";
        return _consoleMessages.Count(message => message.Contains(marker, StringComparison.Ordinal));
    }

    public IReadOnlyList<string> GetHubInvocationMessages(string method)
    {
        var marker = $"[BotNexus:hub] → {method}";
        return _consoleMessages.Where(message => message.Contains(marker, StringComparison.Ordinal)).ToArray();
    }

    public async Task WaitForConsoleMessageAsync(string fragment, int timeoutMs = 15000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (_consoleMessages.Any(message => message.Contains(fragment, StringComparison.Ordinal)))
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for console message containing '{fragment}'.");
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

    /// <summary>Selector for the active channel's message container (replaced #chat-messages).</summary>
    internal const string ActiveChat = ".channel-view.active .channel-messages";

    public Task<int> GetChatMessageCountAsync()
        => Page.Locator($"{ActiveChat} .message").CountAsync();

    public async Task WaitForSystemMessageAsync(string text, int timeoutMs = 15000)
    {
        await Assertions.Expect(Page.Locator($"{ActiveChat} .message.system-msg")).ToContainTextAsync(
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
            var hasStreamingMessage = await IsElementVisibleAsync($"{ActiveChat} .message.assistant.streaming");
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
        await _browserContext.DisposeAsync();
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
    private readonly ConcurrentDictionary<(AgentId AgentId, SessionId SessionId), RecordingAgentHandle> _handles = new();
    private readonly ConcurrentQueue<DispatchRecord> _dispatches = new();
    private readonly ConcurrentDictionary<(AgentId AgentId, SessionId SessionId), AgentInstanceStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RecordingStreamPlan>> _agentPlans = new();
    private readonly ConcurrentDictionary<(AgentId AgentId, SessionId SessionId), ConcurrentQueue<RecordingStreamPlan>> _sessionPlans = new();

    public IReadOnlyList<DispatchRecord> Dispatches => _dispatches.ToList();

    public Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var handle = _handles.GetOrAdd((agentId, sessionId), key => new RecordingAgentHandle(key.AgentId, key.SessionId, this));
        return Task.FromResult<IAgentHandle>(handle);
    }

    public Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        _statuses[(agentId, sessionId)] = AgentInstanceStatus.Stopped;
        return Task.CompletedTask;
    }

    public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId)
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
        => _sessionPlans.GetOrAdd((AgentId.From(agentId), SessionId.From(sessionId)), _ => new ConcurrentQueue<RecordingStreamPlan>()).Enqueue(plan);

    public RecordingStreamPlan GetStreamPlan(string agentId, string sessionId, string message)
    {
        if (_sessionPlans.TryGetValue((AgentId.From(agentId), SessionId.From(sessionId)), out var sessionQueue) &&
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
        => _statuses[(AgentId.From(agentId), SessionId.From(sessionId))] = status;

    public void Reset()
    {
        _dispatches.Clear();
        _agentPlans.Clear();
        _sessionPlans.Clear();
    }
}

internal sealed class RecordingAgentHandle(AgentId agentId, SessionId sessionId, RecordingAgentSupervisor supervisor) : IAgentHandle
{
    private readonly ConcurrentQueue<string> _followUps = new();
    private volatile bool _abortRequested;

    public AgentId AgentId { get; } = agentId;
    public SessionId SessionId { get; } = sessionId;
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

    public Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => FollowUpAsync(message switch
        {
            SubAgentCompletionMessage completion => completion.Content,
            UserMessage user => user.Content,
            _ => message.ToString() ?? string.Empty
        }, cancellationToken);

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

internal sealed class TestSubAgentManager : ISubAgentManager
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, List<SubAgentInfo>> _byParentSession = [];
    private readonly Dictionary<string, SubAgentInfo> _bySubAgentId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string SubAgentId, SessionId RequestingSessionId)> _killRequests = [];

    public IReadOnlyList<(string SubAgentId, SessionId RequestingSessionId)> KillRequests
    {
        get
        {
            lock (_gate)
                return _killRequests.ToList();
        }
    }

    public void SetSubAgents(string parentSessionId, params SubAgentInfo[] subAgents)
    {
        lock (_gate)
        {
            _byParentSession[SessionId.From(parentSessionId)] = subAgents.ToList();
            foreach (var subAgent in subAgents)
                _bySubAgentId[subAgent.SubAgentId] = subAgent;
        }
    }

    public Task<SubAgentInfo> SpawnAsync(SubAgentSpawnRequest request, CancellationToken ct = default)
    {
        var subAgent = new SubAgentInfo
        {
            SubAgentId = $"subagent-{Guid.NewGuid():N}",
            ParentSessionId = request.ParentSessionId,
            ChildSessionId = request.ParentSessionId,
            Name = request.Name,
            Task = request.Task,
            Model = request.ModelOverride,
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        lock (_gate)
        {
            if (!_byParentSession.TryGetValue(request.ParentSessionId, out var existing))
            {
                existing = [];
                _byParentSession[request.ParentSessionId] = existing;
            }

            existing.Add(subAgent);
            _bySubAgentId[subAgent.SubAgentId] = subAgent;
        }

        return Task.FromResult(subAgent);
    }

    public Task<IReadOnlyList<SubAgentInfo>> ListAsync(SessionId parentSessionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byParentSession.TryGetValue(parentSessionId, out var subAgents))
                return Task.FromResult<IReadOnlyList<SubAgentInfo>>(subAgents.ToList());
        }

        return Task.FromResult<IReadOnlyList<SubAgentInfo>>([]);
    }

    public Task<SubAgentInfo?> GetAsync(string subAgentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _bySubAgentId.TryGetValue(subAgentId, out var subAgent);
            return Task.FromResult(subAgent);
        }
    }

    public Task<bool> KillAsync(string subAgentId, SessionId requestingSessionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _killRequests.Add((subAgentId, requestingSessionId));
            if (!_bySubAgentId.TryGetValue(subAgentId, out var current))
                return Task.FromResult(false);

            var updated = current with
            {
                Status = SubAgentStatus.Killed,
                CompletedAt = DateTimeOffset.UtcNow
            };
            _bySubAgentId[subAgentId] = updated;

            if (_byParentSession.TryGetValue(updated.ParentSessionId, out var subAgents))
            {
                for (var i = 0; i < subAgents.Count; i++)
                {
                    if (string.Equals(subAgents[i].SubAgentId, subAgentId, StringComparison.OrdinalIgnoreCase))
                    {
                        subAgents[i] = updated;
                        break;
                    }
                }
            }

            return Task.FromResult(true);
        }
    }

    public Task OnCompletedAsync(string subAgentId, string resultSummary, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_bySubAgentId.TryGetValue(subAgentId, out var current))
                return Task.CompletedTask;

            var updated = current with
            {
                Status = SubAgentStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                ResultSummary = resultSummary
            };
            _bySubAgentId[subAgentId] = updated;

            if (_byParentSession.TryGetValue(updated.ParentSessionId, out var subAgents))
            {
                for (var i = 0; i < subAgents.Count; i++)
                {
                    if (string.Equals(subAgents[i].SubAgentId, subAgentId, StringComparison.OrdinalIgnoreCase))
                    {
                        subAgents[i] = updated;
                        break;
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byParentSession.Clear();
            _bySubAgentId.Clear();
            _killRequests.Clear();
        }
    }
}
