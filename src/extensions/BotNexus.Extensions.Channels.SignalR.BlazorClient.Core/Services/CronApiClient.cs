using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway cron REST API (<c>/api/cron</c>).
/// Returns the merged view of all jobs — both SQLite-persisted (runtime-created)
/// and config-file jobs — so the panel is never limited to appsettings.json alone.
/// </summary>
public sealed class CronApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public CronApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Lists all cron jobs (SQLite + config, system jobs excluded by default).</summary>
    public async Task<(IReadOnlyList<CronJobDto> Jobs, string? Error)> ListAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<CronJobDto>>("/api/cron", JsonOptions);
            return (result as IReadOnlyList<CronJobDto> ?? [], null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>Creates a new cron job in the SQLite store.</summary>
    public async Task<(CronJobDto? Job, string? Error)> CreateAsync(CronJobDto request)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/cron", request, JsonOptions);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CronJobDto>(JsonOptions);
                return (dto, null);
            }
            return (null, await ReadErrorAsync(response));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Updates an existing cron job.</summary>
    public async Task<(CronJobDto? Job, string? Error)> UpdateAsync(string jobId, CronJobDto request)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/api/cron/{Uri.EscapeDataString(jobId)}", request, JsonOptions);
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CronJobDto>(JsonOptions);
                return (dto, null);
            }
            return (null, await ReadErrorAsync(response));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Deletes a cron job by ID.</summary>
    public async Task<(bool Success, string? Error)> DeleteAsync(string jobId)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/cron/{Uri.EscapeDataString(jobId)}");
            if (response.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(response));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Triggers immediate execution of a cron job.</summary>
    public async Task<(bool Success, string? Error)> RunNowAsync(string jobId)
    {
        try
        {
            var response = await _http.PostAsync($"/api/cron/{Uri.EscapeDataString(jobId)}/run", null);
            if (response.IsSuccessStatusCode)
                return (true, null);
            return (false, await ReadErrorAsync(response));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    return errorProp.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    return titleProp.GetString() ?? body;
            }
            return $"HTTP {(int)response.StatusCode}";
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }
}

/// <summary>Cron job data transfer object — mirrors the gateway's CronJob record.</summary>
public sealed class CronJobDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Schedule { get; set; } = string.Empty;
    public string ActionType { get; set; } = "agent-prompt";
    public string? AgentId { get; set; }
    public string? Message { get; set; }
    public string? TemplateName { get; set; }
    public string? Model { get; set; }
    public string? WebhookUrl { get; set; }
    public string? ShellCommand { get; set; }
    public bool Enabled { get; set; } = true;
    public bool System { get; set; }
    public string? TimeZone { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunError { get; set; }
    public string? ConversationId { get; set; }
}
