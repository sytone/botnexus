using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for managing configured gateway locations.
/// </summary>
[ApiController]
[Route("api/locations")]
public sealed class LocationsController(
    PlatformConfigWriter configWriter,
    IOptionsMonitor<PlatformConfig> configOptions,
    IAgentRegistry agentRegistry,
    IEnumerable<IIsolationStrategy> isolationStrategies,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const string RedactedConnectionStringDisplay = "(redacted)";

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Lists all resolved locations.
    /// </summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<LocationResponse>>> List(CancellationToken cancellationToken)
    {
        var config = configOptions.CurrentValue;
        var declaredNames = GetDeclaredLocationNames(config);
        var worldDescriptor = WorldDescriptorBuilder.Build(config, agentRegistry, isolationStrategies);
        var responses = worldDescriptor.Locations
            .Select(location => BuildLocationResponse(
                name: location.Name,
                type: location.Type.Value,
                rawValue: location.Path,
                description: location.Description,
                status: location.Type == LocationType.FileSystem
                    ? (Directory.Exists(location.Path ?? string.Empty) ? "healthy" : "unhealthy")
                    : "unknown",
                isUserDefined: declaredNames.Contains(location.Name)))
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<ActionResult<IReadOnlyList<LocationResponse>>>(Ok(responses));
    }

    /// <summary>
    /// Creates a new user-defined location entry.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<LocationResponse>> Create([FromBody] UpsertLocationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Location name is required." });

        var configEntry = BuildLocationConfig(request, existingConfig: null, out var validationError);
        if (configEntry is null)
            return BadRequest(new { error = validationError });

        var name = request.Name.Trim();
        var duplicate = false;

        // #2134: the existence check, the insert and the write all happen inside the writer lock.
        // Reading the locations map out here first and handing back a finished snapshot is exactly
        // what let two concurrent creates each persist their own stale-plus-one view.
        var saveError = await MutateLocationsAsync(locations =>
        {
            if (TryFindLocationKey(locations, name, out _))
            {
                duplicate = true;
                return $"Location '{name}' already exists.";
            }

            locations[name] = SerializeLocation(configEntry);
            return null;
        }, "before-location-create", cancellationToken);

        if (duplicate)
            return Conflict(new { error = $"Location '{name}' already exists." });
        if (saveError is not null)
            return BadRequest(new { error = saveError });
        await WaitForConfigConditionAsync(
            current => TryGetLocation(current, name, out var reloaded)
                && IsSameLocation(reloaded, configEntry),
            cancellationToken);

        return CreatedAtAction(
            nameof(Get),
            new { name },
            BuildLocationResponse(
                name: name,
                type: configEntry.Type,
                rawValue: ResolveStoredValue(configEntry),
                description: configEntry.Description,
                status: "unknown",
                isUserDefined: true));
    }

    /// <summary>
    /// Gets a single location by name.
    /// </summary>
    [HttpGet("{name}")]
    public Task<ActionResult<LocationResponse>> Get(string name, CancellationToken cancellationToken)
    {
        var config = configOptions.CurrentValue;
        var worldDescriptor = WorldDescriptorBuilder.Build(config, agentRegistry, isolationStrategies);
        var location = worldDescriptor.Locations.FirstOrDefault(loc =>
            string.Equals(loc.Name, name, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return Task.FromResult<ActionResult<LocationResponse>>(NotFound(new { error = $"Location '{name}' was not found." }));

        var isUserDefined = GetDeclaredLocationNames(config).Contains(location.Name);
        return Task.FromResult<ActionResult<LocationResponse>>(Ok(BuildLocationResponse(
            name: location.Name,
            type: location.Type.Value,
            rawValue: location.Path,
            description: location.Description,
            status: "unknown",
            isUserDefined: isUserDefined)));
    }

    /// <summary>
    /// Updates an existing user-defined location.
    /// </summary>
    [HttpPut("{name}")]
    public async Task<ActionResult<LocationResponse>> Update(string name, [FromBody] UpsertLocationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Name)
            && !string.Equals(request.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Location name in payload must match route name." });
        }

        var existingKey = string.Empty;
        LocationConfig? configEntry = null;
        string? validationError = null;
        var missing = false;

        // #2134: read the current entry, rebuild it and write it back all inside the writer lock,
        // so a concurrent create/update/delete of a different location cannot be erased by this
        // save (and cannot make this save operate on a stale value of its own entry).
        var saveError = await MutateLocationsAsync(locations =>
        {
            if (!TryFindLocationKey(locations, name, out existingKey))
            {
                missing = true;
                return $"Location '{name}' was not found.";
            }

            var existingConfig = DeserializeLocation(locations[existingKey]);
            configEntry = BuildLocationConfig(new UpsertLocationRequest
            {
                Name = existingKey,
                Type = request.Type,
                Value = request.Value,
                Description = request.Description
            }, existingConfig, out validationError);

            if (configEntry is null)
                return validationError ?? "Invalid location definition.";

            locations[existingKey] = SerializeLocation(configEntry);
            return null;
        }, "before-location-update", cancellationToken);

        if (missing)
            return NotFound(new { error = $"Location '{name}' was not found." });
        if (configEntry is null)
            return BadRequest(new { error = validationError });
        if (saveError is not null)
            return BadRequest(new { error = saveError });
        await WaitForConfigConditionAsync(
            current => TryGetLocation(current, existingKey, out var reloaded)
                && IsSameLocation(reloaded, configEntry),
            cancellationToken);

        return Ok(BuildLocationResponse(
            name: existingKey,
            type: configEntry.Type,
            rawValue: ResolveStoredValue(configEntry),
            description: configEntry.Description,
            status: "unknown",
            isUserDefined: true));
    }

    /// <summary>
    /// Deletes a user-defined location by name.
    /// </summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        var existingKey = string.Empty;
        var missing = false;

        // #2134: locate-and-remove under the writer lock (see Create/Update).
        var saveError = await MutateLocationsAsync(locations =>
        {
            if (!TryFindLocationKey(locations, name, out existingKey))
            {
                missing = true;
                return $"Location '{name}' was not found.";
            }

            locations.Remove(existingKey);
            return null;
        }, "before-location-remove", cancellationToken);

        if (missing)
            return NotFound(new { error = $"Location '{name}' was not found." });
        if (saveError is not null)
            return BadRequest(new { error = saveError });
        await WaitForConfigConditionAsync(
            current => current.Gateway?.Locations is null
                || !TryFindDictionaryKey(current.Gateway.Locations, existingKey, out _),
            cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Runs a health check for a single location.
    /// </summary>
    [HttpPost("{name}/check")]
    public async Task<ActionResult<LocationHealthCheckResponse>> Check(string name, CancellationToken cancellationToken)
    {
        var config = configOptions.CurrentValue;
        var worldDescriptor = WorldDescriptorBuilder.Build(config, agentRegistry, isolationStrategies);
        var location = worldDescriptor.Locations.FirstOrDefault(loc =>
            string.Equals(loc.Name, name, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return NotFound(new { error = $"Location '{name}' was not found." });

        var result = await CheckLocationAsync(location, cancellationToken);
        return Ok(new LocationHealthCheckResponse
        {
            Name = location.Name,
            Status = result.status,
            Message = result.message
        });
    }

    private async Task<(string status, string message)> CheckLocationAsync(Location location, CancellationToken cancellationToken)
    {
        if (location.Type == LocationType.FileSystem)
        {
            if (string.IsNullOrWhiteSpace(location.Path))
                return ("unhealthy", "Path is missing.");

            return Directory.Exists(location.Path)
                ? ("healthy", "Directory exists.")
                : ("unhealthy", "Directory not found.");
        }

        if (location.Type == LocationType.Database)
        {
            return string.IsNullOrWhiteSpace(location.Path)
                ? ("unhealthy", "Connection string is missing.")
                : ("healthy", "Connection string is configured.");
        }

        if (location.Type == LocationType.Api || location.Type == LocationType.RemoteNode || location.Type == LocationType.McpServer)
        {
            if (string.IsNullOrWhiteSpace(location.Path))
                return ("unhealthy", "Endpoint is missing.");

            if (!Uri.TryCreate(location.Path, UriKind.Absolute, out var uri))
                return ("unhealthy", "Endpoint is not a valid absolute URI.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var client = httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await client.SendAsync(request, cts.Token);
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var fallbackResponse = await client.SendAsync(fallbackRequest, cts.Token);
                    return fallbackResponse.IsSuccessStatusCode
                        ? ("healthy", $"HTTP {((int)fallbackResponse.StatusCode)}")
                        : ("unhealthy", $"HTTP {((int)fallbackResponse.StatusCode)}");
                }

                return response.IsSuccessStatusCode
                    ? ("healthy", $"HTTP {((int)response.StatusCode)}")
                    : ("unhealthy", $"HTTP {((int)response.StatusCode)}");
            }
            catch (OperationCanceledException)
            {
                return ("unhealthy", "Health check timed out.");
            }
            catch (Exception ex)
            {
                return ("unhealthy", ex.Message);
            }
        }

        return ("unknown", "Location type is not supported for health checks.");
    }

    private static bool TryFindDictionaryKey<TValue>(
        Dictionary<string, TValue> dictionary,
        string key,
        out string existingKey)
    {
        if (dictionary.ContainsKey(key))
        {
            existingKey = key;
            return true;
        }

        foreach (var candidate in dictionary.Keys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = candidate;
                return true;
            }
        }

        existingKey = string.Empty;
        return false;
    }

    private static HashSet<string> GetDeclaredLocationNames(PlatformConfig config)
        => config.Gateway?.Locations is null
            ? []
            : config.Gateway.Locations.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool TryGetLocation(PlatformConfig config, string name, out LocationConfig location)
    {
        var locations = config.Gateway?.Locations;
        if (locations is not null && TryFindDictionaryKey(locations, name, out var key))
        {
            location = locations[key];
            return true;
        }

        location = null!;
        return false;
    }

    private static bool IsSameLocation(LocationConfig left, LocationConfig right)
        => string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Path, right.Path, StringComparison.Ordinal)
           && string.Equals(left.Endpoint, right.Endpoint, StringComparison.Ordinal)
           && string.Equals(left.ConnectionString, right.ConnectionString, StringComparison.Ordinal)
           && string.Equals(left.Description, right.Description, StringComparison.Ordinal);

    /// <summary>
    /// Builds the entry to persist, preserving every stored field the request does not model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #3616. This deliberately mutates a COPY of <paramref name="existingConfig"/> rather than
    /// constructing a fresh <see cref="LocationConfig"/>. <see cref="UpsertLocationRequest"/> models
    /// four fields - name, type, value, description - while <see cref="LocationConfig"/> declares
    /// six. Rebuilding from the DTO silently dropped <c>Properties</c>, so editing a location's
    /// description destroyed the extensible settings a consumer of that location depends on.
    /// </para>
    /// <para>
    /// The loss was invisible: the write succeeded, the response was 200, and the change set
    /// genuinely contained only the edited keys - because the entry had already been narrowed
    /// before the differ ever saw it. Same defect class as #3547 on <c>PUT /api/agents/{id}</c>,
    /// where a typed DTO projected over stored configuration deleted everything it did not model.
    /// </para>
    /// <para>
    /// Absent from the payload means "not being changed", never "delete it". The one place that
    /// rule does NOT apply is the type-discriminated value: changing a location's type must clear
    /// the previous type's field, or a filesystem-turned-api location would keep a stale
    /// <c>Path</c> alongside its new <c>Endpoint</c>. That clearing is explicit below.
    /// </para>
    /// </remarks>
    private static LocationConfig? BuildLocationConfig(
        UpsertLocationRequest request,
        LocationConfig? existingConfig,
        out string? error)
    {
        var normalizedName = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            error = "Location name is required.";
            return null;
        }

        var type = (request.Type ?? "filesystem").Trim().ToLowerInvariant();
        var value = request.Value?.Trim();
        if (type == LocationType.Database.Value
            && string.IsNullOrWhiteSpace(value)
            && existingConfig is { Type: var existingType }
            && string.Equals(existingType, LocationType.Database.Value, StringComparison.OrdinalIgnoreCase))
        {
            value = existingConfig?.ConnectionString;
        }

        // Start from what is stored so unmodelled fields survive; on create there is nothing to
        // preserve and this is an empty instance, exactly as before.
        var config = CloneForUpdate(existingConfig);

        config.Type = type;
        config.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        // Type-discriminated value: set the field this type uses and clear the other two, so a
        // type change cannot strand the previous type's value.
        config.Path = null;
        config.Endpoint = null;
        config.ConnectionString = null;

        if (type == LocationType.FileSystem.Value)
            config.Path = value;
        else if (type == LocationType.Database.Value)
            config.ConnectionString = value;
        else
            config.Endpoint = value;

        if (!TryValidateLocationConfig(normalizedName, config, out var validationError))
        {
            error = validationError;
            return null;
        }

        error = null;
        return config;
    }

    /// <summary>
    /// Copies the stored entry so an update starts from persisted state rather than from defaults.
    /// </summary>
    /// <remarks>
    /// Copied field-by-field rather than by serialization round trip: a round trip would silently
    /// acquire any future property, which sounds convenient but would also silently acquire one
    /// that must NOT be carried forward. An explicit list means adding a property to
    /// <see cref="LocationConfig"/> forces a decision here, and the companion fence test fails
    /// until that decision is made.
    /// </remarks>
    private static LocationConfig CloneForUpdate(LocationConfig? existing)
        => existing is null
            ? new LocationConfig()
            : new LocationConfig
            {
                Type = existing.Type,
                Path = existing.Path,
                Endpoint = existing.Endpoint,
                ConnectionString = existing.ConnectionString,
                Description = existing.Description,
                Username = existing.Username,
                CredentialRef = existing.CredentialRef,
                VerifyTls = existing.VerifyTls,
                Tags = existing.Tags,
                Properties = existing.Properties,
            };

    private static string? ResolveStoredValue(LocationConfig config)
        => config.Path ?? config.Endpoint ?? config.ConnectionString;

    private static LocationResponse BuildLocationResponse(
        string name,
        string type,
        string? rawValue,
        string? description,
        string status,
        bool isUserDefined)
    {
        var hasConfiguredSecret = string.Equals(type, LocationType.Database.Value, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rawValue);
        var safeDisplayValue = hasConfiguredSecret ? RedactedConnectionStringDisplay : rawValue;
        return new LocationResponse
        {
            Name = name,
            Type = type,
            PathOrEndpoint = safeDisplayValue,
            Description = description,
            Status = status,
            IsUserDefined = isUserDefined,
            HasConfiguredSecret = hasConfiguredSecret
        };
    }

    /// <summary>
    /// Applies a mutation to the <c>gateway.locations</c> map entirely inside the
    /// <see cref="PlatformConfigWriter"/> lock (issue #2134).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Previously this controller read a whole <see cref="PlatformConfig"/>, mutated the locations
    /// dictionary in memory, and then replaced the entire <c>gateway</c> section with that
    /// precomputed snapshot. The writer lock covered only the final file I/O, so two concurrent
    /// requests both read the same locations map and the second replace silently discarded the
    /// first request's location.
    /// </para>
    /// <para>
    /// The mutation now runs against the live on-disk <c>locations</c> node under the lock, so the
    /// read-modify-write window is inside mutual exclusion and no broad precomputed snapshot is
    /// ever handed to a replacement operation. The writer validates the complete candidate document
    /// before touching the file, so a rejected candidate leaves config.json byte-for-byte unchanged.
    /// </para>
    /// </remarks>
    /// <returns><see langword="null"/> on success, otherwise a caller-presentable error message.</returns>
    private async Task<string?> MutateLocationsAsync(
        Func<JsonObject, string?> mutation,
        string reason,
        CancellationToken cancellationToken)
    {
        var errors = await configWriter.MutateSectionAsync(
            "gateway",
            gateway =>
            {
                if (gateway["locations"] is not JsonObject locations)
                {
                    locations = new JsonObject();
                    gateway["locations"] = locations;
                }

                return mutation(locations);
            },
            reason,
            cancellationToken);

        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    private static JsonObject SerializeLocation(LocationConfig config)
        => JsonSerializer.SerializeToNode(config, WriteJsonOptions) as JsonObject ?? new JsonObject();

    private static LocationConfig? DeserializeLocation(JsonNode? node)
        => node is null
            ? null
            : node.Deserialize<LocationConfig>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    /// <summary>
    /// Case-insensitive key lookup over the raw locations JSON object, matching the semantics
    /// <see cref="TryFindDictionaryKey{TValue}"/> provides for the typed dictionary.
    /// </summary>
    private static bool TryFindLocationKey(JsonObject locations, string key, out string existingKey)
    {
        foreach (var candidate in locations.Select(entry => entry.Key))
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = candidate;
                return true;
            }
        }

        existingKey = string.Empty;
        return false;
    }

    private async Task WaitForConfigConditionAsync(Func<PlatformConfig, bool> predicate, CancellationToken cancellationToken)
    {
        if (predicate(configOptions.CurrentValue))
            return;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
            if (predicate(configOptions.CurrentValue))
                return;
        }
    }

    private static readonly string[] ValidTypes =
    [
        LocationType.FileSystem.Value,
        LocationType.Api.Value,
        LocationType.McpServer.Value,
        LocationType.Database.Value,
        LocationType.RemoteNode.Value
    ];

    private static bool TryValidateLocationConfig(string name, LocationConfig locationConfig, out string error)
    {
        var type = string.IsNullOrWhiteSpace(locationConfig.Type)
            ? "filesystem"
            : locationConfig.Type.Trim();

        if (type.Equals("filesystem", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.Path))
            {
                error = $"gateway.locations.{name}.path is required for filesystem locations.";
                return false;
            }

            try
            {
                _ = Path.GetFullPath(locationConfig.Path);
            }
            catch (Exception)
            {
                error = $"gateway.locations.{name}.path must be a valid path.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (type.Equals("api", StringComparison.OrdinalIgnoreCase)
            || type.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
            || type.Equals("remote-node", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.Endpoint))
            {
                error = $"gateway.locations.{name}.endpoint is required for {type} locations.";
                return false;
            }

            if (!Uri.TryCreate(locationConfig.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                error = $"gateway.locations.{name}.endpoint must be a valid http or https absolute URL.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (type.Equals("database", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(locationConfig.ConnectionString))
            {
                error = $"gateway.locations.{name}.connectionString is required for database locations.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = $"gateway.locations.{name}.type must be one of: {string.Join(", ", ValidTypes)}.";
        return false;
    }

}

/// <summary>
/// Upsert request payload for a location definition.
/// </summary>
public sealed class UpsertLocationRequest
{
    /// <summary>The location name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The location type.</summary>
    public string Type { get; init; } = "filesystem";

    /// <summary>The path, endpoint, or connection string value.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Optional location description.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Location response returned by the locations API.
/// </summary>
public sealed class LocationResponse
{
    /// <summary>The location name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The location type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>The path or endpoint value (redacted placeholder for database connection strings).</summary>
    public string? PathOrEndpoint { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>The current status.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Whether this location is user-defined in config.</summary>
    public bool IsUserDefined { get; init; }

    /// <summary>Whether a secret value exists but is intentionally redacted from the response.</summary>
    public bool HasConfiguredSecret { get; init; }
}

/// <summary>
/// Health check response for a single location.
/// </summary>
public sealed class LocationHealthCheckResponse
{
    /// <summary>The location name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The health status result.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Additional status details.</summary>
    public string Message { get; init; } = string.Empty;
}

