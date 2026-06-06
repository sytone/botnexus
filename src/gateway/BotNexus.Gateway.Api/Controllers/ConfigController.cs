using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for platform configuration diagnostics.
/// </summary>
/// <summary>
/// Represents config controller.
/// </summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Get the effective platform configuration with defaults applied (secrets redacted).
    /// </summary>
    [HttpGet]
    public ActionResult<JsonObject> GetConfig(
        [FromServices] IOptionsMonitor<PlatformConfig> configOptions)
    {
        var effectiveConfig = BuildEffectiveConfig(configOptions.CurrentValue);
        var response = SerializeConfig(effectiveConfig);
        RedactSecrets(response);
        return Ok(response);
    }

    /// <summary>
    /// Get the raw platform configuration from disk (secrets redacted).
    /// </summary>
    [HttpGet("raw")]
    public async Task<ActionResult<JsonObject>> GetRawConfig(
        [FromServices] PlatformConfigWriter writer,
        CancellationToken ct)
    {
        var config = await writer.ReadAsync(ct);
        RedactSecrets(config);
        return Ok(config);
    }

    /// <summary>
    /// Get a specific config section.
    /// </summary>
    [HttpGet("{section}")]
    public async Task<ActionResult<JsonNode?>> GetSection(
        string section,
        [FromServices] PlatformConfigWriter writer,
        CancellationToken ct)
    {
        var config = await writer.ReadAsync(ct);
        if (!config.ContainsKey(section))
            return NotFound();

        var sectionNode = config[section]?.DeepClone();
        if (section.Equals("providers", StringComparison.OrdinalIgnoreCase) && sectionNode is JsonObject providers)
            RedactProviderSecrets(providers);

        return Ok(sectionNode);
    }

    /// <summary>
    /// Update a config section.
    /// </summary>
    [HttpPut("{section}")]
    [RequireAdmin]
    public async Task<ActionResult> UpdateSection(
        string section,
        [FromBody] JsonNode value,
        [FromServices] PlatformConfigWriter writer,
        CancellationToken ct)
    {
        // Prevent updating agents via this endpoint (use /api/agents instead)
        if (section.Equals("agents", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Use /api/agents for agent management.");

        await writer.UpdateSectionAsync(section, value, ct);
        return Ok(new { message = $"Section '{section}' updated. Changes will be applied automatically." });
    }

    /// <summary>
    /// Update a specific entry within a config section (e.g., a single provider).
    /// </summary>
    [HttpPut("{section}/{key}")]
    [RequireAdmin]
    public async Task<ActionResult> UpdateSectionEntry(
        string section,
        string key,
        [FromBody] JsonNode value,
        [FromServices] PlatformConfigWriter writer,
        CancellationToken ct)
    {
        await writer.UpdateSectionEntryAsync(section, key, value, ct);
        return Ok(new { message = $"Entry '{key}' in section '{section}' updated." });
    }

    /// <summary>
    /// Delete an entry from a config section.
    /// </summary>
    [HttpDelete("{section}/{key}")]
    [RequireAdmin]
    public async Task<ActionResult> DeleteSectionEntry(
        string section,
        string key,
        [FromServices] PlatformConfigWriter writer,
        CancellationToken ct)
    {
        await writer.RemoveSectionEntryAsync(section, key, ct);
        return Ok(new { message = $"Entry '{key}' removed from section '{section}'." });
    }

    /// <summary>
    /// Returns the effective (merged) configuration for a specific agent, with provenance per field.
    /// </summary>
    [HttpGet("agents/{agentId}/effective")]
    public async Task<ActionResult<EffectiveAgentConfigResponse>> GetEffectiveAgentConfig(
        string agentId,
        [FromServices] IOptionsMonitor<PlatformConfig> configOptions,
        [FromServices] IConfiguration configuration,
        CancellationToken ct)
    {
        var config = configOptions.CurrentValue;

        // Normalise lookup — defaults is a reserved key, never a real agent
        if (string.Equals(agentId, "defaults", StringComparison.OrdinalIgnoreCase))
            return NotFound($"Agent '{agentId}' not found.");

        if (config.Agents is null || !config.Agents.TryGetValue(agentId, out var agentConfig))
        {
            var fallbackPath = ResolveConfiguredPath(configuration);
            var fallbackConfig = await new PlatformConfigWriter(
                fallbackPath,
                new System.IO.Abstractions.FileSystem()).ReadPlatformConfigAsync(ct);
            if (System.IO.File.Exists(fallbackPath))
            {
                var fallbackConfiguration = new ConfigurationBuilder()
                    .AddJsonFile(fallbackPath, optional: false, reloadOnChange: false)
                    .Build();
                var postConfigure = new PlatformConfigPostConfigure(fallbackConfiguration, fallbackPath);
                postConfigure.PostConfigure(Options.DefaultName, fallbackConfig);
            }
            if (fallbackConfig.Agents is null || !fallbackConfig.Agents.TryGetValue(agentId, out agentConfig))
                return NotFound($"Agent '{agentId}' not found.");

            config = fallbackConfig;
        }

        var defaults = config.AgentDefaults;
        var rawElementNullable = config.AgentRawElements is not null && config.AgentRawElements.TryGetValue(agentId, out var re)
            ? re
            : (JsonElement?)null;

        var effective = AgentConfigMerger.Merge(defaults, agentConfig, rawElementNullable);

        var sources = BuildSources(defaults, agentConfig, rawElementNullable);

        return Ok(new EffectiveAgentConfigResponse
        {
            AgentId = agentId,
            DefaultsApplied = defaults is not null,
            Config = new EffectiveAgentConfigDto
            {
                ToolIds = effective.ToolIds,
                Memory = effective.Memory,
                Heartbeat = effective.Heartbeat,
                FileAccess = effective.FileAccess,
            },
            Sources = sources,
        });
    }

    private static Dictionary<string, string> BuildSources(
        AgentDefaultsConfig? defaults,
        AgentDefinitionConfig agent,
        JsonElement? rawElement)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        // toolIds
        sources["toolIds"] = ResolveListSource("toolIds", defaults?.ToolIds, agent.ToolIds, rawElement);

        // memory.*
        var agentMemObj = GetNestedObject(rawElement, "memory");
        sources["memory.enabled"] = ResolveBoolSource("enabled", defaults?.Memory?.Enabled, agent.Memory?.Enabled, agentMemObj, agent.Memory is null);
        sources["memory.indexing"] = ResolveStringSource("indexing", defaults?.Memory?.Indexing, agent.Memory?.Indexing, agentMemObj, agent.Memory is null);
        sources["memory.promptInjection"] = ResolveStringSource("promptInjection", defaults?.Memory?.PromptInjection, agent.Memory?.PromptInjection, agentMemObj, agent.Memory is null);

        // heartbeat.*
        var agentHbObj = GetNestedObject(rawElement, "heartbeat");
        sources["heartbeat.enabled"] = ResolveBoolSource("enabled", defaults?.Heartbeat?.Enabled, agent.Heartbeat?.Enabled, agentHbObj, agent.Heartbeat is null);
        sources["heartbeat.intervalMinutes"] = ResolveIntSource("intervalMinutes", defaults?.Heartbeat?.IntervalMinutes, agent.Heartbeat?.IntervalMinutes, agentHbObj, agent.Heartbeat is null);

        // fileAccess.*
        var agentFaObj = GetNestedObject(rawElement, "fileAccess");
        sources["fileAccess.allowedReadPaths"] = ResolveListSource("allowedReadPaths", defaults?.FileAccess?.AllowedReadPaths, agent.FileAccess?.AllowedReadPaths, agentFaObj);
        sources["fileAccess.allowedWritePaths"] = ResolveListSource("allowedWritePaths", defaults?.FileAccess?.AllowedWritePaths, agent.FileAccess?.AllowedWritePaths, agentFaObj);
        sources["fileAccess.deniedPaths"] = ResolveListSource("deniedPaths", defaults?.FileAccess?.DeniedPaths, agent.FileAccess?.DeniedPaths, agentFaObj);

        return sources;
    }

    private static JsonElement? GetNestedObject(JsonElement? parent, string key)
    {
        if (parent is null) return null;
        if (!parent.Value.TryGetProperty(key, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Object ? prop : null;
    }

    private static bool HasKey(JsonElement? obj, string key)
        => obj is not null && obj.Value.TryGetProperty(key, out _);

    /// <summary>
    /// Source for a list field (replacement semantics).
    /// </summary>
    private static string ResolveListSource(string key, System.Collections.IEnumerable? defaultVal, System.Collections.IEnumerable? agentVal, JsonElement? agentObj)
    {
        if (HasKey(agentObj, key))
            return "agent";
        if (agentObj is null && agentVal is not null)
            return "agent"; // inferred from value presence without raw JSON
        if (defaultVal is not null)
            return "inherited";
        return "implicit-default";
    }

    private static string ResolveBoolSource(string key, bool? defaultVal, bool? agentVal, JsonElement? agentObj, bool agentSectionAbsent)
    {
        if (!agentSectionAbsent && HasKey(agentObj, key))
            return "agent";
        if (!agentSectionAbsent && agentObj is null && agentVal.HasValue)
            return "agent";
        if (defaultVal.HasValue)
            return "inherited";
        return "implicit-default";
    }

    private static string ResolveStringSource(string key, string? defaultVal, string? agentVal, JsonElement? agentObj, bool agentSectionAbsent)
    {
        if (!agentSectionAbsent && HasKey(agentObj, key))
            return "agent";
        if (!agentSectionAbsent && agentObj is null && agentVal is not null)
            return "agent";
        if (defaultVal is not null)
            return "inherited";
        return "implicit-default";
    }

    private static string ResolveIntSource(string key, int? defaultVal, int? agentVal, JsonElement? agentObj, bool agentSectionAbsent)
    {
        if (!agentSectionAbsent && HasKey(agentObj, key))
            return "agent";
        if (!agentSectionAbsent && agentObj is null && agentVal.HasValue)
            return "agent";
        if (defaultVal.HasValue)
            return "inherited";
        return "implicit-default";
    }

    /// <summary>
    /// Validates the platform configuration file and returns any errors.
    /// </summary>
    /// <param name="path">Optional explicit path to a config file. Defaults to <c>~/.botnexus/config.json</c>.</param>
    /// <param name="configOptions">Current runtime configuration bound through the host options pipeline.</param>
    /// <param name="configuration">Host configuration used to resolve the active config path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The config validation result.</returns>
    [HttpGet("validate")]
    public async Task<ActionResult<ConfigValidationResponse>> Validate(
        [FromQuery] string? path,
        [FromServices] IOptionsMonitor<PlatformConfig> configOptions,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(path)
            ? ResolveConfiguredPath(configuration)
            : Path.GetFullPath(path);

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!System.IO.File.Exists(resolvedPath))
            {
                return Ok(new ConfigValidationResponse(
                    IsValid: false,
                    ConfigPath: resolvedPath,
                    Warnings: [],
                    Errors:
                    [
                        $"Config file not found at '{resolvedPath}'.",
                        "Create ~/.botnexus/config.json (or pass ?path=...) and include gateway/providers/channels/agents sections."
                    ]));
            }

            var current = configOptions.CurrentValue;
            var errors = PlatformConfigLoader.Validate(current)
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(error => error, StringComparer.Ordinal)
                .ToArray();
            var warnings = PlatformConfigLoader.ValidateWarnings(current);
            return Ok(new ConfigValidationResponse(errors.Length == 0, resolvedPath, warnings, errors));
        }

        if (!System.IO.File.Exists(resolvedPath))
        {
            return Ok(new ConfigValidationResponse(
                IsValid: false,
                ConfigPath: resolvedPath,
                Warnings: [],
                Errors:
                [
                    $"Config file not found at '{resolvedPath}'.",
                    "Create ~/.botnexus/config.json (or pass ?path=...) and include gateway/providers/channels/agents sections."
                ]));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = LoadConfigFromPath(resolvedPath);
            var warnings = PlatformConfigLoader.ValidateWarnings(config);
            var errors = PlatformConfigLoader.Validate(config)
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(error => error, StringComparer.Ordinal)
                .ToArray();
            return Ok(new ConfigValidationResponse(errors.Length == 0, resolvedPath, warnings, errors));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            var parseMessage = ex.GetBaseException() is JsonException jsonException
                ? jsonException.Message
                : ex.Message;
            return Ok(new ConfigValidationResponse(false, resolvedPath, [], [$"Invalid JSON in config file: {parseMessage}"]));
        }
    }

    private static string ResolveConfiguredPath(IConfiguration configuration)
    {
        var configuredPath = configuration["BotNexus:ConfigPath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? PlatformConfigLoader.DefaultConfigPath
            : Path.GetFullPath(configuredPath);
    }

    private static PlatformConfig LoadConfigFromPath(string path)
    {
        var fileConfiguration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var config = new PlatformConfig();
        fileConfiguration.Bind(config);
        new PlatformConfigPostConfigure(fileConfiguration, path).PostConfigure(Options.DefaultName, config);
        return config;
    }

    private static PlatformConfig BuildEffectiveConfig(PlatformConfig config)
    {
        var clone = JsonSerializer.Deserialize<PlatformConfig>(
            JsonSerializer.Serialize(config, WriteOptions),
            ReadOptions) ?? new PlatformConfig();
        clone.Cron ??= new CronConfig();
        return clone;
    }

    private static JsonObject SerializeConfig(PlatformConfig config)
        => JsonSerializer.SerializeToNode(config, WriteOptions)?.AsObject() ?? new JsonObject();

    private static void RedactSecrets(JsonObject config)
    {
        if (config["providers"] is JsonObject providers)
            RedactProviderSecrets(providers);
        if (config["apiKey"] is JsonValue)
            config["apiKey"] = "***";
        if (config["gateway"] is JsonObject gateway)
            RedactGatewaySecrets(gateway);
    }

    private static void RedactGatewaySecrets(JsonObject gateway)
    {
        if (gateway["apiKeys"] is JsonObject apiKeys)
            RedactGatewayApiKeys(apiKeys);
        if (gateway["sessionStore"] is JsonObject sessionStore && sessionStore["connectionString"] is JsonValue)
            sessionStore["connectionString"] = "***";
        if (gateway["locations"] is JsonObject locations)
            RedactGatewayLocations(locations);
        if (gateway["crossWorld"] is JsonObject crossWorld)
            RedactCrossWorldSecrets(crossWorld);
    }

    private static void RedactGatewayApiKeys(JsonObject apiKeys)
    {
        foreach (var (_, apiKeyNode) in apiKeys)
        {
            if (apiKeyNode is JsonObject apiKeyObject && apiKeyObject["apiKey"] is JsonValue)
                apiKeyObject["apiKey"] = "***";
        }
    }

    private static void RedactGatewayLocations(JsonObject locations)
    {
        foreach (var (_, locationNode) in locations)
        {
            if (locationNode is JsonObject location && location["connectionString"] is JsonValue)
                location["connectionString"] = "***";
        }
    }

    private static void RedactCrossWorldSecrets(JsonObject crossWorld)
    {
        if (crossWorld["peers"] is JsonObject peers)
        {
            foreach (var (_, peerNode) in peers)
            {
                if (peerNode is JsonObject peer && peer["apiKey"] is JsonValue)
                    peer["apiKey"] = "***";
            }
        }

        if (crossWorld["inbound"] is not JsonObject inbound || inbound["apiKeys"] is not JsonObject inboundApiKeys)
            return;

        foreach (var key in inboundApiKeys.Select(static pair => pair.Key).ToArray())
        {
            if (inboundApiKeys[key] is JsonValue)
                inboundApiKeys[key] = "***";
        }
    }

    private static void RedactProviderSecrets(JsonObject providers)
    {
        foreach (var (_, providerNode) in providers)
        {
            if (providerNode is JsonObject provider && provider.ContainsKey("apiKey"))
                provider["apiKey"] = "***";
        }
    }
}

/// <summary>
/// Result of a platform configuration validation check.
/// </summary>
/// <param name="IsValid">Whether the configuration passed all validation rules.</param>
/// <param name="ConfigPath">Resolved path to the configuration file that was validated.</param>
/// <param name="Warnings">Validation warnings that do not block startup.</param>
/// <param name="Errors">Validation errors, empty when <paramref name="IsValid"/> is <see langword="true"/>.</param>
public sealed record ConfigValidationResponse(
    bool IsValid,
    string ConfigPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
