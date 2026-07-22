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

        // Config hydration is best-effort: a malformed config.json (JSON parse failure) or a
        // read-only config mount (write failure) must never prevent the gateway from starting.
        // The gateway already runs on defaults when config is unreadable; hydration just keeps
        // the on-disk file in sync. Swallow and log so this hosted service can't crash the host.
        //
        // Issue #2114: compute the merge against a working copy first so we only invoke a
        // persistent write when at least one key is actually added. A hydration pass that adds
        // nothing must not back up or rewrite config.json (avoids startup no-op reload storms).
        try
        {
            var working = await _writer.ReadAsync(cancellationToken);
            foreach (var contributor in contributorList)
            {
                var defaults = contributor.GetDefaults();
                if (defaults is null)
                    continue;

                var defaultsJson = JsonSerializer.SerializeToNode(defaults, SerializeOptions);
                if (defaultsJson is not JsonObject defaultsObj)
                    continue;

                addedKeys += MergeAtPath(working, contributor.SectionPath, defaultsObj);
            }

            if (addedKeys > 0)
            {
                // Re-run the identical merge inside the writer's locked read-modify-write so we
                // persist against the authoritative on-disk document, not our detached copy.
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

                        MergeAtPath(root, contributor.SectionPath, defaultsObj);
                    }
                }, "config-hydration", cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Configuration hydration skipped — config.json is not valid JSON. " +
                "The gateway will run on defaults; fix the JSON and restart to persist defaults.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Expected on read-only config mounts (Docker :ro). Log a concise message without the
            // full stack trace — this is a normal, non-fatal degradation, not an error to triage.
            _logger.LogInformation(
                "Configuration hydration skipped — config.json is not writable " +
                "(read-only mount or permission denied: {Reason}). The gateway will run on defaults.",
                ex.Message);
            return;
        }

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
