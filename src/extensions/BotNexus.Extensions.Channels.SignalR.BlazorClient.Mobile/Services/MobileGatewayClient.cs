using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

// ── Minimal wire DTOs ──────────────────────────────────────────────────────────

file sealed record AgentSummaryDto(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("emoji")] string? Emoji = null);

file sealed record ConversationSummaryDto(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("title")] string Title);

file sealed record HistoryMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null);

file sealed record ConversationHistoryResponseDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<HistoryMessage>? Messages,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("hasMore")] bool HasMore);

file sealed record SendMessageResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("channelType")] string? ChannelType);

file sealed record AgentStreamEvent
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("contentDelta")] public string? ContentDelta { get; init; }
    [JsonPropertyName("thinkingContent")] public string? ThinkingContent { get; init; }
    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; init; }
    [JsonPropertyName("toolResult")] public string? ToolResult { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

// ── Main client ────────────────────────────────────────────────────────────────

/// <summary>
/// Handles REST data loading and SignalR streaming for the mobile Blazor WASM client.
/// Self-contained — no dependency on the desktop BlazorClient assembly.
/// </summary>
public sealed class MobileGatewayClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly MobileState _state;
    private HubConnection? _hub;

    public MobileGatewayClient(HttpClient http, MobileState state)
    {
        _http = http;
        _state = state;
    }

    /// <summary>
    /// Loads agents + conversations via REST and connects the SignalR hub.
    /// Safe to call multiple times — subsequent calls are no-ops if already connected.
    /// </summary>
    public async Task InitializeAsync(string gatewayUrl, CancellationToken ct = default)
    {
        if (_hub?.State == HubConnectionState.Connected)
            return;

        var baseUrl = gatewayUrl.TrimEnd('/') + "/";

        // ── REST: agents ──────────────────────────────────────────────────────
        try
        {
            var agents = await _http.GetFromJsonAsync<List<AgentSummaryDto>>($"{baseUrl}api/agents", ct);
            _state.Agents.Clear();
            if (agents is not null)
            {
                foreach (var a in agents)
                    _state.Agents.Add(new AgentOption { AgentId = a.AgentId, DisplayName = a.DisplayName, Emoji = a.Emoji });
            }

            if (_state.ActiveAgentId is null && _state.Agents.Count > 0)
                _state.ActiveAgentId = _state.Agents[0].AgentId;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] Failed to load agents: {ex.Message}");
        }

        // ── REST: conversations for active agent ──────────────────────────────
        if (_state.ActiveAgentId is not null)
        {
            await LoadConversationsAsync(baseUrl, _state.ActiveAgentId, ct);
        }

        // ── SignalR ───────────────────────────────────────────────────────────
        _hub = new HubConnectionBuilder()
            .WithUrl($"{gatewayUrl.TrimEnd('/')}/hub/gateway")
            .WithAutomaticReconnect()
            .Build();

        RegisterHubHandlers();

        try
        {
            await _hub.StartAsync(ct);
            await _hub.InvokeAsync<object>("SubscribeAll", ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] Hub connection failed: {ex.Message}");
        }

        _state.NotifyChanged();
    }

    /// <summary>Load conversations for an agent and optionally select the first one.</summary>
    public async Task LoadConversationsAsync(string baseUrl, string agentId, CancellationToken ct = default)
    {
        try
        {
            var convs = await _http.GetFromJsonAsync<List<ConversationSummaryDto>>(
                $"{baseUrl}api/conversations?agentId={Uri.EscapeDataString(agentId)}", ct);
            _state.Conversations.Clear();
            if (convs is not null)
            {
                foreach (var c in convs)
                    _state.Conversations.Add(new ConversationOption { ConversationId = c.ConversationId, Title = c.Title });
            }

            if (_state.ActiveConversationId is null && _state.Conversations.Count > 0)
                _state.ActiveConversationId = _state.Conversations[0].ConversationId;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] Failed to load conversations: {ex.Message}");
        }

        // ── REST: history for active conversation ─────────────────────────────
        if (_state.ActiveConversationId is not null)
        {
            await LoadHistoryAsync(baseUrl, _state.ActiveConversationId, ct);
        }

        _state.NotifyChanged();
    }

    /// <summary>Load message history for a conversation.</summary>
    public async Task LoadHistoryAsync(string baseUrl, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var history = await _http.GetFromJsonAsync<ConversationHistoryResponseDto>(
                $"{baseUrl}api/conversations/{Uri.EscapeDataString(conversationId)}/history", ct);
            _state.Messages.Clear();
            if (history?.Messages is not null)
            {
                foreach (var m in history.Messages)
                {
                    _state.Messages.Add(new ChatMessage
                    {
                        Role = m.Role,
                        Content = m.Content,
                        ToolName = m.ToolName,
                        IsToolCall = m.ToolName is not null,
                        Timestamp = m.Timestamp
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] Failed to load history: {ex.Message}");
        }
    }

    /// <summary>Send a user message via SignalR.</summary>
    public async Task SendMessageAsync(string content)
    {
        if (_hub?.State != HubConnectionState.Connected || _state.ActiveAgentId is null)
            return;

        // Optimistic: add user message immediately
        _state.Messages.Add(new ChatMessage { Role = "user", Content = content });
        _state.NotifyChanged();

        try
        {
            var result = await _hub.InvokeAsync<SendMessageResult>(
                "SendMessage",
                _state.ActiveAgentId,
                "signalr",
                content,
                _state.ActiveConversationId);

            _state.ActiveSessionId = result.SessionId;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] SendMessage failed: {ex.Message}");
        }
    }

    /// <summary>Reset the current session.</summary>
    public async Task ResetSessionAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || _state.ActiveAgentId is null)
            return;

        try
        {
            await _hub.InvokeAsync("ResetSession", _state.ActiveAgentId);
            _state.Messages.Clear();
            _state.ActiveSessionId = null;
            _state.StreamBuffer = string.Empty;
            _state.IsStreaming = false;
            _state.NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mobile] ResetSession failed: {ex.Message}");
        }
    }

    private void RegisterHubHandlers()
    {
        if (_hub is null) return;

        _hub.On<AgentStreamEvent>("MessageStart", e =>
        {
            _state.IsStreaming = true;
            _state.StreamBuffer = string.Empty;
            _state.NotifyChanged();
        });

        _hub.On<AgentStreamEvent>("ContentDelta", e =>
        {
            _state.StreamBuffer += e.ContentDelta ?? string.Empty;
            _state.NotifyChanged();
        });

        _hub.On<AgentStreamEvent>("ThinkingDelta", e =>
        {
            // Ignore thinking deltas in mobile for now
        });

        _hub.On<AgentStreamEvent>("ToolStart", e =>
        {
            // Could add tool call indicator
        });

        _hub.On<AgentStreamEvent>("ToolEnd", e =>
        {
            if (e.ToolName is not null)
            {
                _state.Messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = e.ToolResult ?? string.Empty,
                    ToolName = e.ToolName,
                    IsToolCall = true
                });
            }
        });

        _hub.On<AgentStreamEvent>("MessageEnd", e =>
        {
            if (!string.IsNullOrEmpty(_state.StreamBuffer))
            {
                _state.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = _state.StreamBuffer
                });
            }
            _state.IsStreaming = false;
            _state.StreamBuffer = string.Empty;
            _state.NotifyChanged();
        });

        _hub.On<AgentStreamEvent>("Error", e =>
        {
            _state.IsStreaming = false;
            _state.StreamBuffer = string.Empty;
            if (e.ErrorMessage is not null)
            {
                _state.Messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = $"⚠ {e.ErrorMessage}"
                });
            }
            _state.NotifyChanged();
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
