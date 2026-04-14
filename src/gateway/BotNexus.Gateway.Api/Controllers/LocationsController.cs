using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for managing configured gateway locations.
/// </summary>
[ApiController]
[Route("api/locations")]
public sealed class LocationsController(
    IConfiguration configuration,
    IAgentRegistry agentRegistry,
    IEnumerable<IIsolationStrategy> isolationStrategies,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
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
    public async Task<ActionResult<IReadOnlyList<LocationResponse>>> List(CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        var declaredNames = GetDeclaredLocationNames(config);
        var worldDescriptor = WorldDescriptorBuilder.Build(config, agentRegistry, isolationStrategies);
        var responses = worldDescriptor.Locations
            .Select(location => new LocationResponse
            {
                Name = location.Name,
                Type = location.Type.Value,
                PathOrEndpoint = location.Path,
                Description = location.Description,
                Status = location.Type == LocationType.FileSystem
                    ? (Directory.Exists(location.Path ?? string.Empty) ? "healthy" : "unhealthy")
                    : "unknown",
                IsUserDefined = declaredNames.Contains(location.Name)
            })
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(responses);
    }

    /// <summary>
    /// Creates a new user-defined location entry.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<LocationResponse>> Create([FromBody] UpsertLocationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Location name is required." });

        var config = await LoadConfigAsync(cancellationToken);
        config.Gateway ??= new GatewaySettingsConfig();
        config.Gateway.Locations ??= new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase);

        if (TryFindDictionaryKey(config.Gateway.Locations, request.Name, out _))
            return Conflict(new { error = $"Location '{request.Name}' already exists." });

        var configEntry = BuildLocationConfig(request, out var validationError);
        if (configEntry is null)
            return BadRequest(new { error = validationError });

        config.Gateway.Locations[request.Name.Trim()] = configEntry;
        var saveError = await SaveConfigAsync(config, cancellationToken);
        if (saveError is not null)
            return BadRequest(new { error = saveError });

        return CreatedAtAction(nameof(Get), new { name = request.Name.Trim() }, new LocationResponse
        {
            Name = request.Name.Trim(),
            Type = configEntry.Type,
            PathOrEndpoint = ResolveDisplayValue(configEntry),
            Description = configEntry.Description,
            Status = "unknown",
            IsUserDefined = true
        });
    }

    /// <summary>
    /// Gets a single location by name.
    /// </summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<LocationResponse>> Get(string name, CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        var worldDescriptor = WorldDescriptorBuilder.Build(config, agentRegistry, isolationStrategies);
        var location = worldDescriptor.Locations.FirstOrDefault(loc =>
            string.Equals(loc.Name, name, StringComparison.OrdinalIgnoreCase));
        if (location is null)
            return NotFound(new { error = $"Location '{name}' was not found." });

        var isUserDefined = GetDeclaredLocationNames(config).Contains(location.Name);
        return Ok(new LocationResponse
        {
            Name = location.Name,
            Type = location.Type.Value,
            PathOrEndpoint = location.Path,
            Description = location.Description,
            Status = "unknown",
            IsUserDefined = isUserDefined
        });
    }

    /// <summary>
    /// Updates an existing user-defined location.
    /// </summary>
    [HttpPut("{name}")]
    public async Task<ActionResult<LocationResponse>> Update(string name, [FromBody] UpsertLocationRequest request, CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        var locations = config.Gateway?.Locations;
        if (locations is null || !TryFindDictionaryKey(locations, name, out var existingKey))
            return NotFound(new { error = $"Location '{name}' was not found." });

        var configEntry = BuildLocationConfig(new UpsertLocationRequest
        {
            Name = existingKey,
            Type = request.Type,
            Value = request.Value,
            Description = request.Description
        }, out var validationError);
        if (configEntry is null)
            return BadRequest(new { error = validationError });

        locations[existingKey] = configEntry;
        var saveError = await SaveConfigAsync(config, cancellationToken);
        if (saveError is not null)
            return BadRequest(new { error = saveError });

        return Ok(new LocationResponse
        {
            Name = existingKey,
            Type = configEntry.Type,
            PathOrEndpoint = ResolveDisplayValue(configEntry),
            Description = configEntry.Description,
            Status = "unknown",
            IsUserDefined = true
        });
    }

    /// <summary>
    /// Deletes a user-defined location by name.
    /// </summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
        var locations = config.Gateway?.Locations;
        if (locations is null || !TryFindDictionaryKey(locations, name, out var existingKey))
            return NotFound(new { error = $"Location '{name}' was not found." });

        locations.Remove(existingKey);
        var saveError = await SaveConfigAsync(config, cancellationToken);
        if (saveError is not null)
            return BadRequest(new { error = saveError });

        return NoContent();
    }

    /// <summary>
    /// Runs a health check for a single location.
    /// </summary>
    [HttpPost("{name}/check")]
    public async Task<ActionResult<LocationHealthCheckResponse>> Check(string name, CancellationToken cancellationToken)
    {
        var config = await LoadConfigAsync(cancellationToken);
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

    private static LocationConfig? BuildLocationConfig(UpsertLocationRequest request, out string? error)
    {
        var type = (request.Type ?? "filesystem").Trim().ToLowerInvariant();
        var value = request.Value?.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Path / endpoint / connection string is required.";
            return null;
        }

        try
        {
            _ = LocationType.FromString(type);
        }
        catch (Exception)
        {
            error = $"Unsupported location type '{request.Type}'.";
            return null;
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

        error = null;
        return config;
    }

    private static string? ResolveDisplayValue(LocationConfig config)
        => config.Path ?? config.Endpoint ?? config.ConnectionString;

    private async Task<PlatformConfig> LoadConfigAsync(CancellationToken cancellationToken)
        => await PlatformConfigLoader.LoadAsync(GetConfigPath(), cancellationToken, validateOnLoad: false);

    private async Task<string?> SaveConfigAsync(PlatformConfig config, CancellationToken cancellationToken)
    {
        var errors = PlatformConfigLoader.Validate(config);
        if (errors.Count > 0)
            return string.Join(Environment.NewLine, errors);

        var configPath = GetConfigPath();
        var configDirectory = Path.GetDirectoryName(configPath) ?? PlatformConfigLoader.DefaultConfigDirectory;
        PlatformConfigLoader.EnsureConfigDirectory(configDirectory);
        await System.IO.File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, WriteJsonOptions),
            cancellationToken);
        return null;
    }

    private string GetConfigPath()
    {
        var configuredPath = configuration["BotNexus:ConfigPath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? PlatformConfigLoader.DefaultConfigPath
            : Path.GetFullPath(configuredPath);
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

    /// <summary>The path or endpoint value.</summary>
    public string? PathOrEndpoint { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>The current status.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Whether this location is user-defined in config.</summary>
    public bool IsUserDefined { get; init; }
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
