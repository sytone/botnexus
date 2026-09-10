using BotNexus.Gateway.Configuration;
using BotNexus.Memory;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Translates the operator's shared-store configuration into what the registry consumes.
/// </summary>
/// <remarks>
/// Two types for one idea, deliberately. <see cref="SharedMemoryStoreEntry"/> is the config
/// surface - nullable everywhere, because a half-filled form is a real state an operator can save
/// - and <see cref="SharedMemoryStoreConfig"/> is the internal record, where a store without a
/// name is not a thing that can exist. This is the seam that turns one into the other, and the
/// place where an unusable entry is dropped rather than allowed to fail later as a null
/// reference somewhere in the registry.
/// </remarks>
public static class SharedMemoryStoreConfigMapping
{
    /// <summary>
    /// Maps configured entries, skipping any that cannot form a usable store.
    /// </summary>
    /// <param name="entries">Entries as configured, possibly null.</param>
    /// <returns>Usable store configurations; empty when nothing is configured.</returns>
    public static IReadOnlyList<SharedMemoryStoreConfig> FromConfig(
        IEnumerable<SharedMemoryStoreEntry>? entries)
    {
        if (entries is null)
            return [];

        var mapped = new List<SharedMemoryStoreConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            // A nameless store cannot be addressed, granted, or written to. Dropping it here beats
            // registering something no tool can ever name.
            if (string.IsNullOrWhiteSpace(entry?.Name))
                continue;

            var name = entry.Name.Trim();

            // The registry resolves stores by name case-insensitively and would silently serve one
            // config's ACL for another's name. First one wins, which matches how the registry
            // itself resolves duplicates.
            if (!seen.Add(name))
                continue;

            mapped.Add(new SharedMemoryStoreConfig
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description.Trim(),
                Readers = Clean(entry.Readers),
                Writers = Clean(entry.Writers),
                RetentionDays = entry.RetentionDays > 0 ? entry.RetentionDays : null
            });
        }

        return mapped;
    }

    /// <summary>
    /// Trims and de-duplicates an access list, dropping blanks.
    /// </summary>
    /// <remarks>
    /// A stray blank entry in a writers list is not harmless: the registry matches on agent id,
    /// and an empty string is an id no agent has - but a whitespace-only one that survived
    /// trimming elsewhere could match nothing while looking like a grant in the UI.
    /// </remarks>
    private static IReadOnlyList<string> Clean(IEnumerable<string>? values) =>
        values is null
            ? []
            : [.. values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
}
