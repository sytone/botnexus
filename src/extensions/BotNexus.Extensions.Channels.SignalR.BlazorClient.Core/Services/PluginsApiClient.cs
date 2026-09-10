using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>Integrity state of an installed plugin's content on disk.</summary>
public enum PluginTrustStateDto
{
    /// <summary>Integrity could not be attested.</summary>
    Unverified = 0,

    /// <summary>Content matches what install recorded.</summary>
    Verified = 1,

    /// <summary>Content diverges from what install recorded.</summary>
    Modified = 2,
}

/// <summary>Whether a newer revision is available at a plugin's source.</summary>
public enum PluginUpdateStateDto
{
    /// <summary>The source was not probed.</summary>
    Unknown = 0,

    /// <summary>The source resolves to the revision already on disk.</summary>
    Current = 1,

    /// <summary>The source resolves to a newer revision.</summary>
    UpdateAvailable = 2,

    /// <summary>Updates are disabled, so the source is deliberately not probed.</summary>
    Pinned = 3,

    /// <summary>The source could not be probed.</summary>
    ProbeFailed = 4,
}

/// <summary>One installed plugin as rendered by the portal plugins page.</summary>
public sealed record PluginRowDto
{
    /// <summary>Plugin identifier; also the <c>/plugins/{PluginId}</c> route parameter.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Marketplace source the content came from.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>Branch or tag requested at install time, or <c>null</c> for the default branch.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>Exact revision currently on disk.</summary>
    [JsonPropertyName("resolvedVersion")]
    public string ResolvedVersion { get; init; } = string.Empty;

    /// <summary>Version the plugin's manifest advertises, or <c>null</c> when unversioned.</summary>
    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; init; }

    /// <summary>Whether scheduled updates may replace this plugin's content.</summary>
    [JsonPropertyName("updatesEnabled")]
    public bool UpdatesEnabled { get; init; }

    /// <summary>When the content on disk was materialised.</summary>
    [JsonPropertyName("installedAtUtc")]
    public DateTimeOffset InstalledAtUtc { get; init; }

    /// <summary>Number of files install recorded.</summary>
    [JsonPropertyName("fileCount")]
    public int FileCount { get; init; }

    /// <summary>Integrity state of the content on disk.</summary>
    [JsonPropertyName("trustState")]
    public PluginTrustStateDto TrustState { get; init; }

    /// <summary>Operator-readable explanation of the trust state.</summary>
    [JsonPropertyName("trustDetail")]
    public string? TrustDetail { get; init; }

    /// <summary>Update availability at the plugin's source.</summary>
    [JsonPropertyName("updateState")]
    public PluginUpdateStateDto UpdateState { get; init; }

    /// <summary>Revision the source resolves to, when it was probed.</summary>
    [JsonPropertyName("availableVersion")]
    public string? AvailableVersion { get; init; }

    /// <summary>Why an update probe failed.</summary>
    [JsonPropertyName("updateProbeError")]
    public string? UpdateProbeError { get; init; }

    /// <summary>Extension this plugin deployed, or <c>null</c> for a skills-only plugin.</summary>
    [JsonPropertyName("deployedExtensionId")]
    public string? DeployedExtensionId { get; init; }

    /// <summary>Whether this plugin's contributed nav entries are hidden from the sidebar.</summary>
    [JsonPropertyName("navHidden")]
    public bool NavHidden { get; init; }
}

/// <summary>Request body for toggling a plugin's auto-update preference.</summary>
public sealed record PluginUpdatePreferenceDto
{
    /// <summary>Whether scheduled updates may replace this plugin's content.</summary>
    [JsonPropertyName("updatesEnabled")]
    public bool UpdatesEnabled { get; init; }
}

/// <summary>Request body for showing or hiding a plugin's contributed nav entries.</summary>
public sealed record PluginNavVisibilityDto
{
    /// <summary>Whether to hide this plugin's nav entries.</summary>
    [JsonPropertyName("navHidden")]
    public bool NavHidden { get; init; }
}

/// <summary>Request body for installing a plugin from a marketplace source.</summary>
public sealed record PluginInstallRequestDto
{
    /// <summary>Repository URL to install from.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>Branch, tag or commit to install, or <c>null</c> for the default branch.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>Whether the caller accepts a plugin that carries a gateway extension.</summary>
    [JsonPropertyName("acknowledgeCarriedExtension")]
    public bool AcknowledgeCarriedExtension { get; init; }
}

/// <summary>Gateway response to an install, update or remove.</summary>
public sealed record PluginOperationResponseDto
{
    /// <summary>Lifecycle outcome name.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Plugin identifier.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Revision now on disk.</summary>
    [JsonPropertyName("resolvedVersion")]
    public string? ResolvedVersion { get; init; }

    /// <summary>Whether a gateway restart is needed before the result takes effect.</summary>
    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; init; }

    /// <summary>The plugin's row, when it is still installed.</summary>
    [JsonPropertyName("plugin")]
    public PluginRowDto? Plugin { get; init; }
}

/// <summary>One field-named failure from the gateway.</summary>
public sealed record PluginErrorDetailDto
{
    /// <summary>Manifest or request field at fault.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; init; }

    /// <summary>Human-readable reason.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Gateway error body for a refused plugin operation.</summary>
public sealed record PluginErrorResponseDto
{
    /// <summary>Primary failure message.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Every failure, each naming its field.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<PluginErrorDetailDto>? Errors { get; init; }
}

/// <summary>
/// Outcome of a plugin lifecycle call, as the page needs to render it.
/// </summary>
/// <remarks>
/// <see cref="ConsentRequired"/> is deliberately its own state rather than a flavour of failure.
/// A refusal for want of consent is the one error the page should re-offer to the operator, and
/// telling it apart from a broken plugin by reading prose would break the moment the wording
/// changed - so it is keyed off the gateway's <c>extension.consent</c> field instead.
/// </remarks>
public sealed record PluginOperationOutcomeDto
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Whether the gateway refused because a carried extension was not acknowledged.</summary>
    public bool ConsentRequired { get; init; }

    /// <summary>Whether a gateway restart is needed before the result takes effect.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Failure message, when the operation did not succeed.</summary>
    public string? Error { get; init; }

    /// <summary>Gateway response on success.</summary>
    public PluginOperationResponseDto? Response { get; init; }
}

/// <summary>One repository the portal looks in to find plugins.</summary>
/// <remarks>
/// Mirrors the gateway's <c>MarketplaceSource</c>. A source is a place to LOOK: it records a URL
/// and what the last read found, and by itself installs nothing.
/// </remarks>
public sealed record MarketplaceSourceDto
{
    /// <summary>Identifier, derived from the URL unless one was given.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Repository URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Branch, tag or commit read, or <c>null</c> for the default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>When the source was added.</summary>
    public DateTimeOffset AddedAtUtc { get; init; }

    /// <summary>When its content was last successfully read, or <c>null</c> if it never was.</summary>
    public DateTimeOffset? LastRefreshedAtUtc { get; init; }

    /// <summary>Why the last read failed, or <c>null</c> when it succeeded.</summary>
    public string? LastError { get; init; }

    /// <summary><c>"catalog"</c>, <c>"plugin"</c>, or <c>null</c> when it could not be read.</summary>
    public string? Kind { get; init; }

    /// <summary>What the source offers; empty until a read succeeds.</summary>
    public IReadOnlyList<MarketplaceOfferingDto> Offerings { get; init; } = [];
}

/// <summary>One plugin a source offers.</summary>
public sealed record MarketplaceOfferingDto
{
    /// <summary>Plugin identifier, from the plugin's own manifest where it could be read.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Repository URL to install from.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Advertised version, or <c>null</c>.</summary>
    public string? Version { get; init; }

    /// <summary>Listing summary, or <c>null</c>.</summary>
    public string? Description { get; init; }

    /// <summary>Branch, tag or commit to install, or <c>null</c> for the default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>
    /// Whether installing this plugin deploys a gateway extension - code loaded in-process at full
    /// trust. Read from the plugin's own manifest, not from any catalog listing it.
    /// </summary>
    public bool CarriesExtension { get; init; }

    /// <summary>Why this entry could not be read, or <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// A problem with the catalog's pinned version - stale or naming no ref - or <c>null</c>.
    /// The entry still lists and installs; the warning is about where the catalog points.
    /// </summary>
    public string? VersionWarning { get; init; }
}

/// <summary>Request body for adding a marketplace source.</summary>
public sealed record MarketplaceSourceRequestDto
{
    /// <summary>Repository URL to read plugins from.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Identifier, or <c>null</c> to derive one from the URL.</summary>
    public string? Name { get; init; }

    /// <summary>Branch, tag or commit to read, or <c>null</c> for the default branch.</summary>
    public string? Reference { get; init; }
}

/// <summary>Result of adding, refreshing or removing a marketplace source.</summary>
/// <remarks>
/// <see cref="Source"/> is carried on success so the page can render the result without a second
/// round trip - including the case where the source was stored but could not be read, which is a
/// success with <see cref="MarketplaceSourceDto.LastError"/> set rather than a failure.
/// </remarks>
public sealed record MarketplaceSourceOutcomeDto
{
    /// <summary>Whether the call succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Failure message, when it did not.</summary>
    public string? Error { get; init; }

    /// <summary>The source as stored, on success.</summary>
    public MarketplaceSourceDto? Source { get; init; }
}

/// <summary>
/// Client for the gateway plugins REST API (<c>/api/plugins</c>, #2687, slice 8 of #2623).
/// </summary>
/// <remarks>
/// The list call swallows transport failures into an empty list so the page renders its own empty
/// state rather than an error boundary. The preference write does NOT swallow: a toggle that
/// silently failed would leave the switch showing a preference the gateway never stored, which is
/// exactly the lie a persistence control must not tell.
/// </remarks>
public sealed class PluginsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// Raised after any change that can alter which plugins contribute left-nav entries, so the
    /// sidebar repaints instead of waiting for a portal reload.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ToolsApiClient.Changed</c>, which exists for the same reason: the management page
    /// and the nav read through one client, so the write side can tell the nav side that its input
    /// moved. Raised only on a SUCCESSFUL write - announcing a change the gateway refused would
    /// make the sidebar disagree with what is stored.
    /// </remarks>
    public event Action? Changed;

    /// <summary>Initialises the client over the portal's configured <see cref="HttpClient"/>.</summary>
    /// <param name="http">Portal HTTP client.</param>
    public PluginsApiClient(HttpClient http) => _http = http;

    /// <summary>Lists every installed plugin, ordered by name. Never returns null.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<PluginRowDto>> ListAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<PluginRowDto>>("/api/plugins", JsonOptions, ct);
        return result ?? [];
    }

    /// <summary>
    /// Sets a plugin's auto-update preference and returns the refreshed row, or <c>null</c> when
    /// the gateway refused the write.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="updatesEnabled">New preference.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginRowDto?> SetUpdatePreferenceAsync(
        string name,
        bool updatesEnabled,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var response = await _http.PutAsJsonAsync(
            $"/api/plugins/{Uri.EscapeDataString(name)}/update-preference",
            new PluginUpdatePreferenceDto { UpdatesEnabled = updatesEnabled },
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<PluginRowDto>(JsonOptions, ct);
    }

    /// <summary>
    /// Shows or hides a plugin's contributed nav entries, returning the refreshed row, or
    /// <c>null</c> when the gateway refused the write.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="navHidden">Whether to hide the entries.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginRowDto?> SetNavVisibilityAsync(
        string name,
        bool navHidden,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var response = await _http.PutAsJsonAsync(
            $"/api/plugins/{Uri.EscapeDataString(name)}/nav-visibility",
            new PluginNavVisibilityDto { NavHidden = navHidden },
            JsonOptions,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var row = await response.Content.ReadFromJsonAsync<PluginRowDto>(JsonOptions, ct);
        Changed?.Invoke();
        return row;
    }

    /// <summary>
    /// Installs a plugin from a repository URL.
    /// </summary>
    /// <param name="source">Repository URL.</param>
    /// <param name="reference">Branch, tag or commit, or <c>null</c> for the default branch.</param>
    /// <param name="acknowledgeCarriedExtension">
    /// Whether the operator has accepted that the plugin may carry code. Left false on the first
    /// attempt: whether a source carries an extension is only knowable after the gateway fetches
    /// it, so consent is asked for in response to a refusal, not guessed at up front.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginOperationOutcomeDto> InstallAsync(
        string source,
        string? reference = null,
        bool acknowledgeCarriedExtension = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new PluginOperationOutcomeDto { Error = "A repository URL is required." };
        }

        using var response = await _http.PostAsJsonAsync(
            "/api/plugins/install",
            new PluginInstallRequestDto
            {
                Source = source.Trim(),
                Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
                AcknowledgeCarriedExtension = acknowledgeCarriedExtension,
            },
            JsonOptions,
            ct);

        return await ReadOutcomeAsync(response, ct, () => Changed?.Invoke());
    }

    /// <summary>Re-resolves a plugin's source and replaces its content if the source moved.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginOperationOutcomeDto> UpdateAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new PluginOperationOutcomeDto { Error = "A plugin name is required." };
        }

        using var response = await _http.PostAsync(
            $"/api/plugins/{Uri.EscapeDataString(name)}/update", content: null, ct);

        return await ReadOutcomeAsync(response, ct, () => Changed?.Invoke());
    }

    /// <summary>Removes an installed plugin and any extension it deployed.</summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PluginOperationOutcomeDto> RemoveAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new PluginOperationOutcomeDto { Error = "A plugin name is required." };
        }

        using var response = await _http.DeleteAsync(
            $"/api/plugins/{Uri.EscapeDataString(name)}", ct);

        return await ReadOutcomeAsync(response, ct, () => Changed?.Invoke());
    }

    /// <summary>
    /// Projects an HTTP response onto the outcome the page renders, preserving the gateway's own
    /// field-named error rather than flattening it to a status code.
    /// </summary>
    private static async Task<PluginOperationOutcomeDto> ReadOutcomeAsync(
        HttpResponseMessage response,
        CancellationToken ct,
        Action? onChanged = null)
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<PluginOperationResponseDto>(JsonOptions, ct);
            onChanged?.Invoke();
            return new PluginOperationOutcomeDto
            {
                Succeeded = true,
                RestartRequired = body?.RestartRequired ?? false,
                Response = body,
            };
        }

        PluginErrorResponseDto? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<PluginErrorResponseDto>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            // A non-JSON error body is still a failure; it just cannot name a field.
        }

        var consentRequired = error?.Errors?.Any(e =>
            string.Equals(e.Field, "extension.consent", StringComparison.Ordinal)) ?? false;

        return new PluginOperationOutcomeDto
        {
            Succeeded = false,
            ConsentRequired = consentRequired,
            Error = error?.Error ?? $"The gateway refused the request ({(int)response.StatusCode}).",
        };
    }

    /// <summary>
    /// Raised after any change to the configured marketplace sources, so a browse view repaints.
    /// </summary>
    public event Action? SourcesChanged;

    /// <summary>Lists configured marketplace sources, ordered by name. Never returns null.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<MarketplaceSourceDto>> ListSourcesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<MarketplaceSourceDto>>(
            "/api/plugins/sources", JsonOptions, ct);
        return result ?? [];
    }

    /// <summary>
    /// Adds a repository to look in.
    /// </summary>
    /// <remarks>
    /// A source that was stored but could not be read comes back as a SUCCESS carrying
    /// <see cref="MarketplaceSourceDto.LastError"/>. The distinction matters to the page: the
    /// repository is now configured either way, and reporting it as a failure would invite the
    /// operator to add it again and hit a duplicate conflict.
    /// </remarks>
    /// <param name="url">Repository URL.</param>
    /// <param name="reference">Branch, tag or commit, or <c>null</c> for the default branch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MarketplaceSourceOutcomeDto> AddSourceAsync(
        string url,
        string? reference = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new MarketplaceSourceOutcomeDto { Error = "A repository URL is required." };
        }

        using var response = await _http.PostAsJsonAsync(
            "/api/plugins/sources",
            new MarketplaceSourceRequestDto
            {
                Url = url.Trim(),
                Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            },
            JsonOptions,
            ct);

        return await ReadSourceOutcomeAsync(response, ct);
    }

    /// <summary>Re-reads one source and returns what it now offers.</summary>
    /// <param name="name">Source identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MarketplaceSourceOutcomeDto> RefreshSourceAsync(
        string name,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new MarketplaceSourceOutcomeDto { Error = "A source name is required." };
        }

        using var response = await _http.PostAsync(
            $"/api/plugins/sources/{Uri.EscapeDataString(name)}/refresh", content: null, ct);

        return await ReadSourceOutcomeAsync(response, ct);
    }

    /// <summary>Re-reads every configured source. Never returns null.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<MarketplaceSourceDto>> RefreshAllSourcesAsync(
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/api/plugins/sources/refresh", content: null, ct);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        SourcesChanged?.Invoke();
        return await response.Content.ReadFromJsonAsync<List<MarketplaceSourceDto>>(JsonOptions, ct) ?? [];
    }

    /// <summary>
    /// Removes a source. Plugins installed from it stay installed - this forgets where they were
    /// found, it does not uninstall them.
    /// </summary>
    /// <param name="name">Source identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MarketplaceSourceOutcomeDto> RemoveSourceAsync(
        string name,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new MarketplaceSourceOutcomeDto { Error = "A source name is required." };
        }

        using var response = await _http.DeleteAsync(
            $"/api/plugins/sources/{Uri.EscapeDataString(name)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            return await ReadSourceOutcomeAsync(response, ct);
        }

        SourcesChanged?.Invoke();
        return new MarketplaceSourceOutcomeDto { Succeeded = true };
    }

    /// <summary>Projects a source response onto the outcome the page renders.</summary>
    private static async Task<MarketplaceSourceOutcomeDto> ReadSourceOutcomeAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<MarketplaceSourceDto>(JsonOptions, ct);
            return new MarketplaceSourceOutcomeDto { Succeeded = true, Source = body };
        }

        PluginErrorResponseDto? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<PluginErrorResponseDto>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            // A non-JSON error body is still a failure; it just cannot name a field.
        }

        return new MarketplaceSourceOutcomeDto
        {
            Succeeded = false,
            Error = error?.Error ?? $"The gateway refused the request ({(int)response.StatusCode}).",
        };
    }
}

