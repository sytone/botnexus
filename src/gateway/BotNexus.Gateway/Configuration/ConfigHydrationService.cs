using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Hosted service that hydrates config.json with default values from all registered
/// <see cref="IConfigSchemaContributor"/> implementations on startup. Existing user
/// values are never overwritten — only missing keys are populated.
/// </summary>
public sealed class ConfigHydrationService : IHostedService
{
    private readonly PlatformConfigWriter _writer;
    private readonly IEnumerable<IConfigSchemaContributor> _contributors;
    private readonly ILogger<ConfigHydrationService> _logger;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ConfigHydrationService(
        PlatformConfigWriter writer,
        IEnumerable<IConfigSchemaContributor> contributors,
        ILogger<ConfigHydrationService> logger)
    {
        _writer = writer;
        _contributors = contributors;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var contributorList = _contributors.ToList();
        if (contributorList.Count == 0)
            return;

        int addedKeys = 0;

        await _writer.MutateAsync(root =>
        {
            foreach (var contributor in contributorList)
            {
                var defaults = contributor.GetDefaults();
                if (defaults is null)
                    continue;

                var defaultsJson = JsonSerializer.SerializeToNode(defaults, SerializeOptions);
                if (defaultsJson is not JsonObject defaultsObj)
                    continue;

                addedKeys += MergeAtPath(root, contributor.SectionPath, defaultsObj);
            }
        }, "config-hydration", cancellationToken);

        if (addedKeys > 0)
            _logger.LogInformation("Configuration hydrated: {Count} new keys added", addedKeys);
        else
            _logger.LogDebug("Configuration hydration complete — no new keys needed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Navigates to the target path in the root object, creating intermediate objects as needed,
    /// then deep-merges the defaults. Returns the count of keys added.
    /// </summary>
    internal static int MergeAtPath(JsonObject root, string sectionPath, JsonObject defaults)
    {
        var segments = sectionPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var target = root;

        foreach (var segment in segments)
        {
            if (target[segment] is JsonObject existing)
            {
                target = existing;
            }
            else if (target[segment] is null)
            {
                var newObj = new JsonObject();
                target[segment] = newObj;
                target = newObj;
            }
            else
            {
                // Target path exists but is not an object (scalar or array) — do not overwrite
                return 0;
            }
        }

        return DeepMergeDefaults(target, defaults);
    }

    /// <summary>
    /// Deep-merges <paramref name="defaults"/> into <paramref name="target"/>.
    /// Only adds keys that are missing in target. Never overwrites existing values.
    /// Returns count of keys added.
    /// </summary>
    internal static int DeepMergeDefaults(JsonObject target, JsonObject defaults)
    {
        int added = 0;

        foreach (var kvp in defaults)
        {
            var key = kvp.Key;
            var defaultValue = kvp.Value;

            if (target[key] is null)
            {
                // Key missing entirely — add the default
                target[key] = defaultValue?.DeepClone();
                added += CountKeys(defaultValue);
            }
            else if (target[key] is JsonObject targetChild && defaultValue is JsonObject defaultChild)
            {
                // Both are objects — recurse
                added += DeepMergeDefaults(targetChild, defaultChild);
            }
            // Else: key exists with a value (scalar, array, or null set by user) — preserve it
        }

        return added;
    }

    /// <summary>
    /// Counts the total number of leaf keys in a JSON node tree.
    /// </summary>
    private static int CountKeys(JsonNode? node)
    {
        if (node is null)
            return 1; // null is a valid value, counts as one key

        if (node is JsonObject obj)
        {
            int count = 0;
            foreach (var kvp in obj)
                count += CountKeys(kvp.Value);
            return count == 0 ? 1 : count;
        }

        return 1; // scalar or array = 1 key
    }
}
