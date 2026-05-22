using System.Text.Json;

namespace BotNexus.Conversation.Tests;

/// <summary>
/// Typed HTTP client wrapper for the Conversation REST API.
/// </summary>
public class ConversationApiClient(HttpClient http)
{
    public Task<HttpResponseMessage> GetConversationsAsync(string? agentId = null, CancellationToken ct = default)
    {
        var url = agentId is not null
            ? $"/api/conversations?agentId={Uri.EscapeDataString(agentId)}"
            : "/api/conversations";
        return http.GetAsync(url, ct);
    }

    public Task<HttpResponseMessage> GetConversationAsync(string conversationId, CancellationToken ct = default) =>
        http.GetAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}", ct);

    public Task<HttpResponseMessage> CreateConversationAsync(string agentId, string? title = null, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { agentId, title });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return http.PostAsync("/api/conversations", content, ct);
    }

    public Task<HttpResponseMessage> GetConversationHistoryAsync(string conversationId, CancellationToken ct = default) =>
        http.GetAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}/history", ct);

    public Task<HttpResponseMessage> GetConversationBindingsAsync(string conversationId, CancellationToken ct = default) =>
        http.GetAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}/bindings", ct);

    public Task<HttpResponseMessage> AddBindingAsync(
        string conversationId,
        string channelType,
        string channelAddress,
        string threadingMode = "Single",
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { channelType, channelAddress, threadingMode });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return http.PostAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}/bindings", content, ct);
    }

    public Task<HttpResponseMessage> DeleteBindingAsync(string conversationId, string bindingId, CancellationToken ct = default) =>
        http.DeleteAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}/bindings/{Uri.EscapeDataString(bindingId)}", ct);

    public Task<HttpResponseMessage> PatchConversationAsync(string conversationId, string? title, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { title });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/conversations/{Uri.EscapeDataString(conversationId)}") { Content = content };
        return http.SendAsync(request, ct);
    }

    public Task<HttpResponseMessage> CreateConversationRawAsync(object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return http.PostAsync("/api/conversations", content, ct);
    }

    public Task<HttpResponseMessage> AddBindingFullAsync(
        string conversationId,
        string channelType,
        string channelAddress,
        string? threadId,
        string? threadingMode,
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { channelType, channelAddress, threadId, threadingMode });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return http.PostAsync($"/api/conversations/{Uri.EscapeDataString(conversationId)}/bindings", content, ct);
    }
}
