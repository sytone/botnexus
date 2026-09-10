using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>One notification as the portal renders it.</summary>
public sealed class NotificationDto
{
    /// <summary>Stable identifier.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>What the notification is about.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

    /// <summary>How much attention it wants: Info, Warning or Error.</summary>
    [JsonPropertyName("severity")] public string Severity { get; set; } = string.Empty;

    /// <summary>One-line summary.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>Optional detail.</summary>
    [JsonPropertyName("body")] public string? Body { get; set; }

    /// <summary>Agent concerned, when any.</summary>
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }

    /// <summary>Conversation concerned, when any.</summary>
    [JsonPropertyName("conversationId")] public string? ConversationId { get; set; }

    /// <summary>Site-relative path to whatever the notification is about.</summary>
    [JsonPropertyName("link")] public string? Link { get; set; }

    /// <summary>When it was raised.</summary>
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When it was read, or null while unread.</summary>
    [JsonPropertyName("readAtUtc")] public DateTimeOffset? ReadAtUtc { get; set; }

    /// <summary>Whether this notification has not been read.</summary>
    [JsonIgnore] public bool IsUnread => ReadAtUtc is null;
}

/// <summary>Unread badge count.</summary>
public sealed class UnreadCountDto
{
    /// <summary>Number of unread notifications.</summary>
    [JsonPropertyName("count")] public int Count { get; set; }
}

/// <summary>
/// Client for the gateway notifications API.
/// </summary>
/// <remarks>
/// Reads swallow transport failures and return empty, so a sidebar badge never becomes an error
/// boundary over something advisory. WRITES do not swallow: a "mark read" that silently failed
/// would leave the portal showing a state the gateway never stored - and since read state is
/// server-side precisely so it is shared with other devices, a lie here is a lie everywhere.
/// </remarks>
public sealed class NotificationsApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = http;

    /// <summary>Raised after a change that alters the unread count, so a badge can repaint.</summary>
    public event Action? Changed;

    /// <summary>Lists notifications, newest first. Never returns null.</summary>
    /// <param name="includeRead">Include notifications already read.</param>
    /// <param name="limit">Maximum to return.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(
        bool includeRead = true,
        int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<NotificationDto>>(
                $"/api/notifications?includeRead={(includeRead ? "true" : "false")}&limit={limit}",
                JsonOptions,
                ct);

            return result ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Unread count for the badge. Returns 0 on any failure.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<UnreadCountDto>("/api/notifications/unread-count", JsonOptions, ct);
            return result?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Marks one notification read. Returns whether the gateway accepted it.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> MarkReadAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        using var response = await _http.PostAsync(
            $"/api/notifications/{Uri.EscapeDataString(id)}/read", content: null, ct);

        if (response.IsSuccessStatusCode)
            Changed?.Invoke();

        return response.IsSuccessStatusCode;
    }

    /// <summary>Marks everything read. Returns whether the gateway accepted it.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> MarkAllReadAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/api/notifications/read-all", content: null, ct);

        if (response.IsSuccessStatusCode)
            Changed?.Invoke();

        return response.IsSuccessStatusCode;
    }

    /// <summary>Deletes one notification. Returns whether the gateway accepted it.</summary>
    /// <param name="id">Notification identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        using var response = await _http.DeleteAsync($"/api/notifications/{Uri.EscapeDataString(id)}", ct);

        if (response.IsSuccessStatusCode)
            Changed?.Invoke();

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Asks the gateway to raise a test notification. Returns whether it accepted the request.
    /// </summary>
    /// <remarks>
    /// Everything else here reads notifications; this is the one call that makes one happen. It
    /// exists because every real notification is raised by a failure, so without it the only way
    /// to find out whether being told works is to break something.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> SendTestAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/api/notifications/test", content: null, ct);

        // No Changed here: the notification comes back over the push like any other, and raising
        // the event too would double-count the badge against a gateway that pushed it first.
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Announces a notification that arrived over SignalR, so the badge repaints without a fetch.
    /// </summary>
    public void NotifyRaised() => Changed?.Invoke();
}
