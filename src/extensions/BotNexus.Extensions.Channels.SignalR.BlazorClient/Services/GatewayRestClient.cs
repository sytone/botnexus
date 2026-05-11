using System.Net.Http.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// All portal REST traffic. Implements <see cref="IGatewayRestClient"/>.
/// Base URL must be set via <see cref="Configure"/> before any calls are made.
/// </summary>
public sealed class GatewayRestClient : IGatewayRestClient
{
    private readonly HttpClient _http;
    private string? _apiBaseUrl;

    public GatewayRestClient(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public string? ApiBaseUrl => _apiBaseUrl;

    /// <inheritdoc />
    public void Configure(string apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/') + "/";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = await _http.GetFromJsonAsync<List<AgentSummary>>(
            $"{_apiBaseUrl}agents",
            cancellationToken);
        return result as IReadOnlyList<AgentSummary> ?? [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = await _http.GetFromJsonAsync<List<ConversationSummaryDto>>(
            $"{_apiBaseUrl}conversations?agentId={Uri.EscapeDataString(agentId)}",
            cancellationToken);
        return result as IReadOnlyList<ConversationSummaryDto> ?? [];
    }

    /// <inheritdoc />
    public async Task<ConversationHistoryResponseDto?> GetHistoryAsync(
        string conversationId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await _http.GetFromJsonAsync<ConversationHistoryResponseDto>(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/history?limit={limit}&offset={offset}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationResponseDto?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await _http.GetFromJsonAsync<ConversationResponseDto>(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var query = string.IsNullOrWhiteSpace(agentId)
            ? string.Empty
            : $"?agentId={Uri.EscapeDataString(agentId)}";
        var result = await _http.GetFromJsonAsync<List<SessionSummary>>(
            $"{_apiBaseUrl}sessions{query}",
            cancellationToken);
        return result as IReadOnlyList<SessionSummary> ?? [];
    }

    /// <inheritdoc />
    public async Task<SessionHistoryResponseDto?> GetSessionHistoryAsync(
        string sessionId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await _http.GetFromJsonAsync<SessionHistoryResponseDto>(
            $"{_apiBaseUrl}sessions/{Uri.EscapeDataString(sessionId)}/history?limit={limit}&offset={offset}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationResponseDto?> CreateConversationAsync(
        CreateConversationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var response = await _http.PostAsJsonAsync($"{_apiBaseUrl}conversations", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConversationResponseDto>(cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task RenameConversationAsync(
        string conversationId,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var request = new PatchConversationRequestDto(newTitle);
        var response = await _http.PatchAsJsonAsync(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private void EnsureConfigured()
    {
        if (_apiBaseUrl is null)
            throw new InvalidOperationException(
                "GatewayRestClient has not been configured. Call Configure(apiBaseUrl) before making REST calls.");
    }

    /// <inheritdoc />
    public async Task<bool> ArchiveConversationAsync(string conversationId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var response = await _http.DeleteAsync(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}", ct);
        return response.IsSuccessStatusCode;
    }
}
