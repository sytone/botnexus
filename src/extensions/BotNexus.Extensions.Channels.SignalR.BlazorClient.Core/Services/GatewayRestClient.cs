using System.Net.Http.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// All portal REST traffic. Implements <see cref="IGatewayRestClient"/>.
/// Base URL must be set via <see cref="Configure"/> before any calls are made.
/// </summary>
public sealed class GatewayRestClient : IGatewayRestClient, IChannelErrorReporter
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
    public async Task<string?> GetConversationCanvasAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default)
    {
        var url = $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/conversations/{Uri.EscapeDataString(conversationId)}/canvas";
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetConversationTodoAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default)
    {
        var url = $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/conversations/{Uri.EscapeDataString(conversationId)}/todo";
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetConversationPendingAskUserAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default)
    {
        var url = $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/conversations/{Uri.EscapeDataString(conversationId)}/pending-ask-user";
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

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
    public async Task<IReadOnlyList<ConversationSummaryDto>> GetAllConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = await _http.GetFromJsonAsync<List<ConversationSummaryDto>>(
            $"{_apiBaseUrl}conversations",
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
    public async Task<SessionPageDto> GetSessionsAsync(
        string? agentId = null,
        int? limit = null,
        int offset = 0,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        // #2532: the endpoint filters in the store and returns an object carrying totalCount and
        // hasMore. Paging arguments are only emitted when supplied so the historical
        // single-argument call produces the same URL it always did.
        var args = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(agentId))
            args.Add($"agentId={Uri.EscapeDataString(agentId)}");
        if (limit is not null)
            args.Add($"limit={limit.Value}");
        if (offset > 0)
            args.Add($"offset={offset}");
        if (!string.IsNullOrWhiteSpace(conversationId))
            args.Add($"conversationId={Uri.EscapeDataString(conversationId)}");

        var query = args.Count == 0 ? string.Empty : "?" + string.Join("&", args);
        var result = await _http.GetFromJsonAsync<SessionPageDto>(
            $"{_apiBaseUrl}sessions{query}",
            cancellationToken);
        return result ?? new SessionPageDto([], 0, false);
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
    public async Task<CommandResultDto?> ExecuteCommandAsync(
        CommandExecuteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var response = await _http.PostAsJsonAsync($"{_apiBaseUrl}commands/execute", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<CommandResultDto>(cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            // #2873: a transport failure must surface as a visible error at the call site, never as
            // a silent fall-through that sends the command text to the agent instead.
            return null;
        }
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

    /// <inheritdoc />
    public async Task<bool> PinConversationAsync(string conversationId, bool pinned, CancellationToken ct = default)
    {
        EnsureConfigured();
        var url = $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/pin";
        // POST pins, DELETE unpins — mirrors the ConversationsController Pin/Unpin endpoints.
        var response = pinned
            ? await _http.PostAsync(url, content: null, ct)
            : await _http.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async Task<ConversationResponseDto?> SetConversationOverrideAsync(
        string conversationId,
        SetConversationOverrideRequestDto request,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var response = await _http.PutAsJsonAsync(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/override",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConversationResponseDto>(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationResponseDto?> ClearConversationOverrideAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var response = await _http.DeleteAsync(
            $"{_apiBaseUrl}conversations/{Uri.EscapeDataString(conversationId)}/override",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConversationResponseDto>(cancellationToken);
    }
    /// <inheritdoc />
    public async Task<WorkspaceResponseDto?> GetWorkspaceAsync(
        string agentId,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildWorkspaceRequestPath(agentId, path);

        return await _http.GetFromJsonAsync<WorkspaceResponseDto>(requestPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteWorkspaceItemAsync(
        string agentId,
        string path,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildWorkspaceRequestPath(agentId, path);
        if (force)
            requestPath += "?force=true";
        var response = await _http.DeleteAsync(requestPath, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async Task<bool> WriteWorkspaceFileAsync(
        string agentId,
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildWorkspaceRequestPath(agentId, path);
        var response = await _http.PutAsJsonAsync(
            requestPath,
            new { content },
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportListItemDto>> GetReportsAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var result = await _http.GetFromJsonAsync<ReportsListResponseDto>(
            $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/reports",
            cancellationToken);

        return result?.Reports ?? [];
    }

    /// <inheritdoc />
    public async Task<ReportContentDto?> GetReportAsync(
        string agentId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildReportsRequestPath(agentId, fileName);
        return await _http.GetFromJsonAsync<ReportContentDto>(requestPath, cancellationToken);
    }

    private string BuildWorkspaceRequestPath(string agentId, string? path)
    {
        var requestPath = $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/workspace";
        if (string.IsNullOrWhiteSpace(path))
            return requestPath;

        var normalizedPath = path.Trim().Replace('\\', '/').Trim('/');
        if (normalizedPath.Length == 0)
            return requestPath;

        var encodedSegments = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"{requestPath}/{string.Join("/", encodedSegments)}";
    }

    private string BuildReportsRequestPath(string agentId, string fileName)
    {
        var requestPath = $"{_apiBaseUrl}agents/{Uri.EscapeDataString(agentId)}/reports";
        if (string.IsNullOrWhiteSpace(fileName))
            return requestPath;

        var normalizedFileName = fileName.Trim().Replace('\\', '/').Trim('/');
        if (normalizedFileName.Length == 0)
            return requestPath;

        var encodedSegments = normalizedFileName
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"{requestPath}/{string.Join("/", encodedSegments)}";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionDetailDto>> GetExtensionDetailsAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        try
        {
            var result = await _http.GetFromJsonAsync<List<ExtensionDetailDto>>(
                $"{_apiBaseUrl}extensions/details",
                cancellationToken);
            return result as IReadOnlyList<ExtensionDetailDto> ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceResponseDto?> GetSkillsAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildSkillsRequestPath(path);
        return await _http.GetFromJsonAsync<WorkspaceResponseDto>(requestPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WriteSkillFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildSkillsRequestPath(path);
        var response = await _http.PutAsJsonAsync(requestPath, new { content }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSkillItemAsync(
        string path,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var requestPath = BuildSkillsRequestPath(path);
        if (force)
            requestPath += "?force=true";
        var response = await _http.DeleteAsync(requestPath, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private string BuildSkillsRequestPath(string? path)
    {
        var requestPath = $"{_apiBaseUrl}skills";
        if (string.IsNullOrWhiteSpace(path))
            return requestPath;

        var normalizedPath = path.Trim().Replace('\\', '/').Trim('/');
        if (normalizedPath.Length == 0)
            return requestPath;

        var encodedSegments = normalizedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"{requestPath}/{string.Join("/", encodedSegments)}";
    }


    /// <inheritdoc />
    public async Task ReportChannelErrorAsync(ChannelErrorReportDto report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiBaseUrl))
            return;

        try
        {
            var url = string.Concat(_apiBaseUrl, "diagnostics/channel-error");
            await _http.PostAsJsonAsync(url, report, cancellationToken);
        }
        catch
        {
            // Best-effort: never throw from error reporting.
        }
    }

    /// <inheritdoc cref="IChannelErrorReporter.ReportAsync" />
    Task IChannelErrorReporter.ReportAsync(ChannelErrorReportDto report, CancellationToken cancellationToken)
        => ReportChannelErrorAsync(report, cancellationToken);

    /// <inheritdoc />
    public async Task<SessionDebugSnapshotDto?> GetSessionDebugAsync(
        string sessionId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await _http.GetFromJsonAsync<SessionDebugSnapshotDto>(
            $"{_apiBaseUrl}sessions/{Uri.EscapeDataString(sessionId)}/debug?offset={offset}&limit={limit}",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubAgentInfo>> ListSessionSubAgentsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return await _http.GetFromJsonAsync<IReadOnlyList<SubAgentInfo>>(
            $"{_apiBaseUrl}sessions/{Uri.EscapeDataString(sessionId)}/subagents",
            cancellationToken) ?? [];
    }
}