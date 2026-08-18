namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>
/// Trust posture applied to a plugin before its declared MCP servers are started.
/// </summary>
/// <remarks>
/// The member names deliberately mirror <c>BotNexus.Extensions.Skills.Security.SkillTrustMode</c>.
/// Plugins reuse the skills trust model rather than introducing a second one - a second trust
/// vocabulary is how the enforced set and the reported set drift apart. This enum exists here only
/// so the MCP extension does not have to take a project reference on the skills extension for
/// three symbols; <see cref="IPluginTrustEvaluator"/> is the seam the real
/// <c>SkillTrustVerifier</c>-backed implementation plugs into. A fence test pins the two vocabularies
/// together so they cannot drift.
/// </remarks>
public enum PluginTrustMode
{
    /// <summary>No verification - every plugin's servers are registered.</summary>
    Disabled,

    /// <summary>Verification failures are logged but the servers are still registered.</summary>
    Warn,

    /// <summary>Verification failures block registration.</summary>
    Enforce,
}

/// <summary>Whether a plugin's materialised content matched its trust catalog.</summary>
/// <param name="Trusted">Whether verification succeeded.</param>
/// <param name="Reason">Why verification failed, or <c>null</c> when it succeeded.</param>
public sealed record PluginTrustDecision(bool Trusted, string? Reason)
{
    /// <summary>A trusted result.</summary>
    public static PluginTrustDecision Trust { get; } = new(true, null);

    /// <summary>An untrusted result carrying the reason.</summary>
    /// <param name="reason">Why the plugin is not trusted.</param>
    public static PluginTrustDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether an installed plugin's content is trusted.
/// </summary>
/// <remarks>
/// Registration consumes a trust decision rather than computing one. Hashing plugin content here
/// would be a second implementation of the catalog verification that already exists for skills, and
/// the two would eventually disagree about what "trusted" means.
/// </remarks>
public interface IPluginTrustEvaluator
{
    /// <summary>Current trust posture.</summary>
    PluginTrustMode Mode { get; }

    /// <summary>Verifies one plugin's materialised content.</summary>
    /// <param name="pluginName">Plugin identifier.</param>
    /// <param name="pluginDirectory">Absolute directory holding the plugin's content.</param>
    PluginTrustDecision Evaluate(string pluginName, string pluginDirectory);
}

/// <summary>
/// Trust evaluator used when verification is switched off entirely.
/// </summary>
public sealed class DisabledPluginTrustEvaluator : IPluginTrustEvaluator
{
    /// <summary>Shared instance.</summary>
    public static DisabledPluginTrustEvaluator Instance { get; } = new();

    /// <inheritdoc />
    public PluginTrustMode Mode => PluginTrustMode.Disabled;

    /// <inheritdoc />
    public PluginTrustDecision Evaluate(string pluginName, string pluginDirectory)
        => PluginTrustDecision.Trust;
}
