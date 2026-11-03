using System.Collections.Immutable;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration;

/// <summary>Owns rejection messages and safe post-commit warnings captured under the mutation locks.</summary>
public sealed record ConfigMutationResult
{
    /// <summary>Copies both collections; rejected and unchanged edits carry no save warnings.</summary>
    public ConfigMutationResult(IEnumerable<string> errors, IEnumerable<ConfigurationProjectionWarning> warnings)
    {
        Errors = errors.ToImmutableArray();
        Warnings = warnings.ToImmutableArray();
    }

    /// <summary>Rejection messages; an empty collection means the mutation was accepted.</summary>
    public ImmutableArray<string> Errors { get; }

    /// <summary>Safe diagnostics belonging only to this mutation's committed output work.</summary>
    public ImmutableArray<ConfigurationProjectionWarning> Warnings { get; }
}
