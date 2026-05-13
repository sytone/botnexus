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

        var config = await configWriter.ReadPlatformConfigAsync(cancellationToken);
        config.Gateway ??= new GatewaySettingsConfig();
        config.Gateway.Locations ??= new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase);

        if (TryFindDictionaryKey(config.Gateway.Locations, request.Name, out _))
            return Conflict(new { error = $"Location '{request.Name}' already exists." });

        var configEntry = BuildLocationConfig(request, existingConfig: null, out var validationError);
        if (configEntry is null)
            return BadRequest(new { error = validationError });

        config.Gateway.Locations[request.Name.Trim()] = configEntry;
        var saveError = await SaveConfigAsync(config, cancellationToken);
        if (saveError is not null)
            return BadRequest(new { error = saveError });
        await WaitForConfigConditionAsync(
            current => TryGetLocation(current, request.Name.Trim(), out var reloaded)
                && IsSameLocation(reloaded, configEntry),
            cancellationToken);

        return CreatedAtAction(
            nameof(Get),
            new { name = request.Name.Trim() },
            BuildLocationResponse(
                name: request.Name.Trim(),
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

        var config = await configWriter.ReadPlatformConfigAsync(cancellationToken);
        var locations = config.Gateway?.Locations;
        if (locations is null || !TryFindDictionaryKey(locations, name, out var existingKey))
            return NotFound(new { error = $"Location '{name}' was not found." });

        var existingConfig = locations[existingKey];
        var configEntry = BuildLocationConfig(new UpsertLocationRequest
        {
            Name = existingKey,
            Type = request.Type,
            Value = request.Value,
            Description = request.Description
        }, existingConfig, out var validationError);
        if (configEntry is null)
            return BadRequest(new { error = validationError });

        locations[existingKey] = configEntry;
        var saveError = await SaveConfigAsync(config, cancellationToken);
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
        var config = await configWriter.ReadPlatformConfigAsync(cancellationToken);
        var locations = config.Gateway?.Locations;
        if (locations is null || !TryFindDictionaryKey(locations, name, out var existingKey))
            return NotFound(new { error = $"Location '{name}' was not found." });

        locations.Remove(existingKey);
        var saveError = await SaveConfigAsync(config, cancellationToken);
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

        var config = new LocationConfig
        {
            Type = type,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()
        };

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

    private async Task<string?> SaveConfigAsync(PlatformConfig config, CancellationToken cancellationToken)
    {
        var errors = PlatformConfigLoader.Validate(config);
        if (errors.Count > 0)
            return string.Join(Environment.NewLine, errors);

        var root = await configWriter.ReadAsync(cancellationToken);
        if (root["gateway"] is not JsonObject gatewaySection)
        {
            gatewaySection = new JsonObject();
            root["gateway"] = gatewaySection;
        }

        if (config.Gateway is null)
        {
            gatewaySection.Remove("locations");
        }
        else
        {
            var gatewayNode = JsonSerializer.SerializeToNode(config.Gateway, WriteJsonOptions) as JsonObject ?? new JsonObject();
            if (gatewayNode["locations"] is null)
                gatewaySection.Remove("locations");
            else
                gatewaySection["locations"] = gatewayNode["locations"]!.DeepClone();
        }

        var candidateJson = root.ToJsonString(WriteJsonOptions);
        PlatformConfig candidateConfig;
        try
        {
            candidateConfig = JsonSerializer.Deserialize<PlatformConfig>(
                candidateJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PlatformConfig();
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON while preparing config update: {ex.Message}";
        }

        var candidateErrors = PlatformConfigLoader.Validate(candidateConfig);
        if (candidateErrors.Count > 0)
            return string.Join(Environment.NewLine, candidateErrors);

        await configWriter.UpdateSectionAsync("gateway", gatewaySection.DeepClone(), cancellationToken);
        return null;
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

