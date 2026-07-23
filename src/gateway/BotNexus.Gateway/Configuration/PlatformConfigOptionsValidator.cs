using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Validates <see cref="PlatformConfig"/> through the options pipeline on startup,
/// providing fast-fail behavior for misconfigured gateways.
/// </summary>
/// <remarks>
/// <para>
/// Server-side validation is unified on the annotated model (#1613, config parity PBI 5/6 of
/// #1579): the per-field DataAnnotations and the cross-field <c>IValidatableObject</c> escape
/// hatch are both enforced by <see cref="PlatformConfigLoader.ValidateAnnotated"/>, which runs
/// <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/>. The structural
/// JSON-schema check (<see cref="PlatformConfigSchema.ValidateObject"/>) is retained alongside it
/// to catch shape errors the typed model cannot express; the same DataAnnotations now also appear
/// in that generated schema, so the rules are readable client-side as well.
/// </para>
/// <para>
/// #2102 (generalising #2050): a single malformed <em>named</em> agent descriptor must never fail
/// the GLOBAL options result, because <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// re-runs this validator on <c>CurrentValue</c> and a hard failure there throws
/// <see cref="OptionsValidationException"/> everywhere the config is read - including the
/// <c>BeforeToolCall</c> tool-policy hook, which then blocks <em>every</em> tool (exec, write,
/// update_agent, ...) for the whole session and traps the agent in a denial loop where it cannot
/// even repair the bad descriptor. Instead, any error scoped to a specific named agent instance
/// (<c>agents.&lt;id&gt;.*</c> or <c>schema.agents.&lt;id&gt;.*</c>) is quarantined here: the
/// invalid descriptor is already skipped with a warning at load time by
/// <see cref="PlatformConfigAgentSource"/>, so the platform degrades gracefully to the remaining
/// good descriptors rather than denying all tools. Gateway/provider/channel/cron errors and the
/// reserved <c>agents.defaults</c> pseudo-agent (whose values seed every agent) still fail hard.
/// </para>
/// </remarks>
public sealed class PlatformConfigOptionsValidator : IValidateOptions<PlatformConfig>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformConfig options)
    {
        var errors = new List<string>();
        errors.AddRange(PlatformConfigSchema.ValidateObject(options)
            .Where(error => !IsQuarantinableAgentError(error)));
        errors.AddRange(PlatformConfigLoader.ValidateAnnotated(options)
            .Where(error => !IsQuarantinableAgentError(error)));

        var distinctErrors = errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinctErrors.Length > 0
            ? ValidateOptionsResult.Fail(distinctErrors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="error"/> is scoped to a specific
    /// <em>named</em> agent descriptor instance and can therefore be quarantined (the descriptor is
    /// skipped at load) rather than failing the whole options result. Errors for the reserved
    /// <c>agents.defaults</c> pseudo-agent are NOT quarantinable because those values seed every
    /// agent, and errors that are not agent-scoped (gateway, providers, channels, cron) stay hard.
    /// </summary>
    internal static bool IsQuarantinableAgentError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        // Strip the optional structural JSON-schema prefix so both surfaces are matched the same
        // way: "schema.agents.coder.thinking: ..." and "agents.coder.provider is required ...".
        var scoped = error.StartsWith("schema.", StringComparison.OrdinalIgnoreCase)
            ? error["schema.".Length..]
            : error;

        const string prefix = "agents.";
        if (!scoped.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Extract the agent-id segment: everything up to the next '.' after "agents.".
        var remainder = scoped[prefix.Length..];
        var dotIndex = remainder.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex <= 0)
            return false;

        var agentId = remainder[..dotIndex];

        // The reserved defaults pseudo-agent seeds every agent; its errors must fail hard.
        if (agentId.Equals("defaults", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
