using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace BotNexus.Integration.Tests;

/// <summary>
/// SignalR test client that mirrors WebUI behavior (hub.js, events.js, chat.js, session-store.js).
/// All event registration, connection lifecycle, and message flow matches the real JS client.
/// </summary>
public class TestSignalRClient : IAsyncDisposable
{
    // All events the WebUI registers (events.js) — must match exactly
    private static readonly string[] AllHubEvents =
    [
        "Connected", "SessionReset",
        "MessageStart", "ContentDelta", "ThinkingDelta",
        "ToolStart", "ToolEnd", "MessageEnd", "Error",
        "SubAgentSpawned", "SubAgentCompleted", "SubAgentFailed", "SubAgentKilled"
    ];

    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<string, List<ReceivedEvent>> _events = new();
    private readonly ConcurrentDictionary<string, bool> _streamingSessions = new();
    private readonly TestLogger _log;
    private string? _activeSessionId;

    public TestSignalRClient(string baseUrl, TestLogger log)
    {
        _log = log;

        // Match WebUI hub.js:46-50
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hub/gateway?clientVersion=integration-test")
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)])
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .Build();

        RegisterEventHandlers();
        RegisterLifecycleHandlers();
    }

    public string? ActiveSessionId => _activeSessionId;

    private void RegisterEventHandlers()
    {
        foreach (var method in AllHubEvents)
        {
            _connection.On<JsonElement>(method, payload =>
            {
                var sessionId = ExtractSessionId(payload);
                var contentDelta = payload.TryGetProperty("contentDelta", out var cd)
                    ? cd.GetString() : null;

                var evt = new ReceivedEvent(method, DateTimeOffset.UtcNow, payload, contentDelta);
                var eventList = _events.GetOrAdd(sessionId, _ => []);
                lock (eventList) { eventList.Add(evt); }

                // Track streaming state per session (like WebUI events.js)
                if (method == "MessageStart")
                    _streamingSessions[sessionId] = true;
                else if (method is "MessageEnd" or "Error")
                    _streamingSessions.TryRemove(sessionId, out _);

                _log.Write($"📨 [{method}] sid={Truncate(sessionId, 12)} {FormatDelta(contentDelta)}");
            });
        }
    }

    private void RegisterLifecycleHandlers()
    {
        _connection.Reconnecting += _ =>
        {
            _log.Write("⚠️  Connection lost. Reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _log.Write("✅ Reconnected. Re-subscribing...");
            try
            {
                await _connection.InvokeAsync<JsonElement>("SubscribeAll");
                _log.Write("✅ Re-subscribed after reconnect.");
            }
            catch (Exception ex)
            {
                _log.Write($"❌ Re-subscribe failed: {ex.Message}");
            }
        };

        _connection.Closed += ex =>
        {
            _log.Write($"❌ Connection closed: {ex?.Message ?? "clean"}");
            return Task.CompletedTask;
        };
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _connection.StartAsync(ct);
        _log.Write("Connected to hub.");
    }

    public async Task<JsonElement> SubscribeAllAsync(CancellationToken ct)
    {
        var result = await _connection.InvokeAsync<JsonElement>("SubscribeAll", ct);
        _log.Write($"SubscribeAll OK");
        return result;
    }

    /// <summary>
    /// Mirrors hubInvoke('SendMessage') with connection-state guard (hub.js:19-24).
    /// </summary>
    public async Task<SendMessageResult> SendMessageAsync(
        string agentId, string content, CancellationToken ct, string channelType = "signalr")
    {
        if (_connection.State != HubConnectionState.Connected)
        {
            _log.Write($"⚠️  SKIP SendMessage — state: {_connection.State}");
            throw new InvalidOperationException($"Cannot send: connection state is {_connection.State}");
        }

        var result = await _connection.InvokeAsync<JsonElement>("SendMessage", agentId, channelType, content, ct);
        var sessionId = result.GetProperty("sessionId").GetString()
            ?? throw new Exception("No sessionId in SendMessage response");
        var returnedAgentId = result.TryGetProperty("agentId", out var aid) ? aid.GetString() : agentId;
        var returnedChannel = result.TryGetProperty("channelType", out var cht) ? cht.GetString() : channelType;

        _activeSessionId = sessionId;
        _log.Write($"→ SendMessage agent={agentId} session={Truncate(sessionId, 12)}");

        return new SendMessageResult(sessionId, returnedAgentId ?? agentId, returnedChannel ?? channelType);
    }

    public async Task SteerAsync(string agentId, string sessionId, string content, CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("Steer", agentId, sessionId, content, ct);
        _log.Write($"→ Steer agent={agentId} session={Truncate(sessionId, 12)}");
    }

    public async Task FollowUpAsync(string agentId, string sessionId, string content, CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("FollowUp", agentId, sessionId, content, ct);
        _log.Write($"→ FollowUp agent={agentId} session={Truncate(sessionId, 12)}");
    }

    public async Task AbortAsync(string agentId, string sessionId, CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("Abort", agentId, sessionId, ct);
        _log.Write($"→ Abort agent={agentId} session={Truncate(sessionId, 12)}");
    }

    public async Task ResetSessionAsync(string agentId, string sessionId, CancellationToken ct)
    {
        await _connection.InvokeAsync("ResetSession", agentId, sessionId, ct);
        _log.Write($"↺ ResetSession agent={agentId} session={Truncate(sessionId, 12)}");
    }

    public IReadOnlyList<ReceivedEvent> GetEvents(string sessionId)
    {
        if (!_events.TryGetValue(sessionId, out var list)) return [];
        lock (list) { return list.ToList().AsReadOnly(); }
    }

    public bool IsStreaming(string sessionId) =>
        _streamingSessions.TryGetValue(sessionId, out var s) && s;

    public async Task<ReceivedEvent> WaitForEventAsync(string sessionId, string eventType,
        TimeSpan timeout, CancellationToken ct)
    {
        _log.Write($"⏳ Waiting for '{eventType}' on sid={Truncate(sessionId, 12)}");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var events = GetEvents(sessionId);
            var match = events.FirstOrDefault(e => e.Method == eventType);
            if (match is not null)
                return match;
            await Task.Delay(100, ct);
        }

        _log.Write($"⚠️  TIMEOUT: {eventType} on sid={Truncate(sessionId, 12)}");
        _log.Write($"    Events: {GetEvents(sessionId).Count}");
        foreach (var e in GetEvents(sessionId))
            _log.Write($"      [{e.Method}] at {e.ReceivedAt:HH:mm:ss.fff}");
        _log.Write($"    All sessions: {string.Join(", ", _events.Keys.Select(k => Truncate(k, 8)))}");

        throw new TimeoutException($"Timed out waiting for {eventType} on session {Truncate(sessionId, 12)}");
    }

    public async Task<IReadOnlyList<ReceivedEvent>> WaitForMessageCompleteAsync(
        string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        _log.Write($"⏳ WaitForMessageComplete on sid={Truncate(sessionId, 12)}");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var events = GetEvents(sessionId);
            if (events.Any(e => e.Method == "MessageStart") &&
                events.Any(e => e.Method is "MessageEnd" or "Error"))
            {
                _log.Write($"✅ MessageComplete sid={Truncate(sessionId, 12)} ({events.Count} events)");
                return events;
            }
            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"Message did not complete on session {Truncate(sessionId, 12)}");
    }

    public void AssertEventOrder(string sessionId, params string[] expectedOrder)
    {
        var events = GetEvents(sessionId);
        var methods = events.Select(e => e.Method).ToList();
        var lastIndex = -1;
        foreach (var expected in expectedOrder)
        {
            var idx = methods.FindIndex(lastIndex + 1, m => m == expected);
            if (idx < 0)
                throw new Exception($"Event order: expected '{expected}' after index {lastIndex}. " +
                    $"Got: [{string.Join(", ", methods)}]");
            lastIndex = idx;
        }
    }

    private static string ExtractSessionId(JsonElement payload)
    {
        if (!payload.TryGetProperty("sessionId", out var sid)) return "unknown";
        return sid.ValueKind switch
        {
            JsonValueKind.String => sid.GetString() ?? "unknown",
            JsonValueKind.Object when sid.TryGetProperty("value", out var val) => val.GetString() ?? "unknown",
            _ => "unknown"
        };
    }

    private static string Truncate(string s, int len) =>
        s.Length <= len ? s : s[..len] + "...";

    private static string FormatDelta(string? d) =>
        d is not null ? $"delta=\"{Truncate(d, 50)}\"" : "";

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public record SendMessageResult(string SessionId, string AgentId, string ChannelType);
public record ReceivedEvent(string Method, DateTimeOffset ReceivedAt, JsonElement Payload, string? ContentDelta);
