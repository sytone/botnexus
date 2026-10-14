namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Trusted host policy naming additional ambient variables permitted in local tool children.
/// Stores names only, never their values. An absent policy permits only OS essentials.
/// </summary>
public sealed class LocalChildEnvironmentPolicy
{
    private readonly string[] _passThroughNames;

    /// <summary>The secure default: no additional ambient variables.</summary>
    public static LocalChildEnvironmentPolicy Default { get; } = new();

    /// <summary>
    /// Snapshots exact names supplied by the operator. Null means no additional variables.
    /// Invalid names (including wildcard patterns) throw <see cref="ArgumentException"/>.
    /// </summary>
    public LocalChildEnvironmentPolicy(IEnumerable<string>? passThroughNames = null)
    {
        _passThroughNames = passThroughNames?.ToArray() ?? [];
        if (_passThroughNames.Any(name => !IsValidName(name)))
            throw new ArgumentException("Pass-through entries must be exact environment variable names, not patterns.", nameof(passThroughNames));
    }

    internal IEnumerable<string> PassThroughNames => _passThroughNames;

    /// <summary>Accepts nonempty exact names; rejects whitespace, patterns, NUL and assignment syntax.</summary>
    public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && !name.Any(c => char.IsWhiteSpace(c) || c is '\0' or '=' or '*' or '?' or '[' or ']');
}
