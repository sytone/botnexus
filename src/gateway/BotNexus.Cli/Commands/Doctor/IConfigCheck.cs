using System.Text.Json.Nodes;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Represents a single configuration migration/health check for <c>botnexus doctor config</c>.
/// </summary>
public interface IConfigCheck
{
    /// <summary>Stable identifier used in output and dry-run reporting.</summary>
    string Id { get; }

    /// <summary>Human-readable description of what this check validates.</summary>
    string Description { get; }

    /// <summary>One-line explanation of what the fix will apply.</summary>
    string FixDescription { get; }

    /// <summary>Returns true when the config is missing this check's expected value.</summary>
    bool IsApplicable(JsonObject root);

    /// <summary>Applies the fix to the config object in-place.</summary>
    void Apply(JsonObject root);
}
