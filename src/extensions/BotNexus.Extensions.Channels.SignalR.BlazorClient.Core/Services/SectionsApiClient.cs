using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Client for the gateway user-defined conversation sections REST API
/// (<c>/api/agents/{agentId}/sections</c>, issue #2124). All section state - names, order, collapsed
/// preference, and conversation assignments - lives server-side per agent/world, so this client is
/// the portal's only source of truth for custom sidebar groupings; nothing is cached in browser
/// local storage.
/// </summary>
public sealed class SectionsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Initialises the client over the portal's configured <see cref="HttpClient"/>.</summary>
    public SectionsApiClient(HttpClient http) => _http = http;

    private static string Base(string agentId) => $"/api/agents/{Uri.EscapeDataString(agentId)}/sections";

    /// <summary>Lists the agent's sections and the conversation-to-section assignment map.</summary>
    public async Task<SectionListDto> ListAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<SectionListDto>(Base(agentId), JsonOptions, ct);
            return result ?? SectionListDto.Empty;
        }
        catch
        {
            return SectionListDto.Empty;
        }
    }

    /// <summary>Creates a new section for the agent. Returns the created section, or null on failure.</summary>
    public async Task<SectionDto?> CreateAsync(string agentId, string name, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(Base(agentId), new { name }, JsonOptions, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<SectionDto>(JsonOptions, ct)
                : null;
        }
        catch { return null; }
    }

    /// <summary>Renames a section and/or sets its collapsed preference.</summary>
    public async Task<bool> UpdateAsync(string agentId, string sectionId, string? name, bool? isCollapsed, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PatchAsJsonAsync(
                $"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}",
                new { name, isCollapsed },
                JsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Reorders the agent's sections to match the supplied id sequence.</summary>
    public async Task<bool> ReorderAsync(string agentId, IReadOnlyList<string> sectionIds, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"{Base(agentId)}/order",
                new { sectionIds },
                JsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Deletes a section; its conversations return to their system section.</summary>
    public async Task<bool> DeleteAsync(string agentId, string sectionId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Assigns a conversation to a section (replacing any prior assignment).</summary>
    public async Task<bool> AssignAsync(string agentId, string sectionId, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsync(
                $"{Base(agentId)}/{Uri.EscapeDataString(sectionId)}/conversations/{Uri.EscapeDataString(conversationId)}",
                content: null, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Removes a conversation from its section, returning it to its system section.</summary>
    public async Task<bool> UnassignAsync(string agentId, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync(
                $"{Base(agentId)}/conversations/{Uri.EscapeDataString(conversationId)}", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

/// <summary>Sections plus the conversation-id to section-id assignment map for an agent.</summary>
public sealed class SectionListDto
{
    /// <summary>Shared empty instance for error / no-data paths.</summary>
    public static readonly SectionListDto Empty = new();

    /// <summary>The agent's user-defined sections in display order.</summary>
    [JsonPropertyName("sections")]
    public IReadOnlyList<SectionDto> Sections { get; set; } = [];

    /// <summary>Map of conversation id to the section id it is assigned to.</summary>
    [JsonPropertyName("assignments")]
    public IReadOnlyDictionary<string, string> Assignments { get; set; } = new Dictionary<string, string>();
}

/// <summary>Wire representation of a user-defined conversation section.</summary>
public sealed class SectionDto
{
    /// <summary>Stable unique section identifier.</summary>
    [JsonPropertyName("sectionId")] public string SectionId { get; set; } = string.Empty;

    /// <summary>The owning agent id.</summary>
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = string.Empty;

    /// <summary>Display name shown as the section header.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Zero-based ordering position among the agent's sections.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>Whether the section renders collapsed.</summary>
    [JsonPropertyName("isCollapsed")] public bool IsCollapsed { get; set; }
}
