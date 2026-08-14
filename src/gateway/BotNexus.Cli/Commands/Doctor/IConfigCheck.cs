using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Represents a single configuration migration/health check for <c>botnexus doctor config</c>.
/// </summary>
/// <remarks>
/// #2887: checks address configuration by canonical dotted path through
/// <see cref="ConfigDocument"/>. They previously received the raw <c>JsonObject</c> and each
/// hand-rolled its own traversal, which is how two of them ended up reading a root-level
/// <c>compaction</c> block the binder never looks at (#2764). A check can no longer express a
/// traversal at all, so it cannot express a wrong one.
/// </remarks>
public interface IConfigCheck
{
    /// <summary>Stable identifier used in output and dry-run reporting.</summary>
    string Id { get; }

    /// <summary>Human-readable description of what this check validates.</summary>
    string Description { get; }

    /// <summary>One-line explanation of what the fix will apply.</summary>
    string FixDescription { get; }

    /// <summary>Returns true when the config is missing this check's expected value.</summary>
    bool IsApplicable(ConfigDocument config);

    /// <summary>Applies the fix to the config document in-place.</summary>
    void Apply(ConfigDocument config);
}
