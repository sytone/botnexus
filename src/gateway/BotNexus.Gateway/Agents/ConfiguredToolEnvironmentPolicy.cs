using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// <see cref="IToolEnvironmentPolicy"/> over the operator's
/// <c>gateway.toolEnvironmentPassThrough</c> setting.
/// </summary>
/// <remarks>
/// Blank and duplicate names are dropped here rather than at each spawn site. A whitespace-only
/// entry left behind by an editor is not harmless: it would look like a grant in the configuration
/// UI while matching no variable at all, which is the same class of silent no-op that
/// <c>SharedMemoryStoreConfigMapping</c> exists to prevent for shared-memory ACLs.
/// </remarks>
public sealed class ConfiguredToolEnvironmentPolicy : IToolEnvironmentPolicy
{
    /// <summary>Creates the policy from the configured names, which may be null or empty.</summary>
    public ConfiguredToolEnvironmentPolicy(IEnumerable<string>? passThroughVariables)
        => PassThroughVariables = passThroughVariables is null
            ? []
            : [.. passThroughVariables
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<string> PassThroughVariables { get; }
}
