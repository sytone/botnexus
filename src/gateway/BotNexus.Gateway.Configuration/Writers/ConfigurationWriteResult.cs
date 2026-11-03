using System.Collections.Immutable;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>Owns the safe output diagnostics of one completed authority save.</summary>
public sealed record ConfigurationWriteResult
{
    /// <summary>Copies diagnostics so later operations cannot change a held save outcome.</summary>
    public ConfigurationWriteResult(IEnumerable<ConfigurationProjectionWarning> warnings)
        => Warnings = warnings.ToImmutableArray();

    /// <summary>Safe post-commit diagnostics in backup, projection-permission, refresh order.</summary>
    public ImmutableArray<ConfigurationProjectionWarning> Warnings { get; }

    /// <summary>A completed operation with no output diagnostics, including an empty change set.</summary>
    public static ConfigurationWriteResult Empty { get; } = new([]);
}
