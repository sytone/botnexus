using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// #3037: severity is additionally keyed on <em>survivability</em>, not only on agent scope. A
/// purely structural schema error such as <c>NoAdditionalPropertiesAllowed</c> describes a key the
/// typed model does not bind at all - <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// binding ignores unmapped keys and has no mechanism to fail on them, so the bound
/// <see cref="PlatformConfig"/> is identical whether the key is present or absent. Aborting startup
/// on it produces strictly less working software while changing nothing about the configuration in
/// effect. Such errors are downgraded to a single warning per offending path (see
/// <see cref="IsSurvivableStructuralError"/>); errors describing a value that IS bound remain fatal.
/// </para>
/// </remarks>
public sealed class PlatformConfigOptionsValidator : IValidateOptions<PlatformConfig>
{
    private readonly ILogger _logger;

    /// <summary>
    /// Source of structural schema errors. Defaults to <see cref="PlatformConfigSchema.ValidateObject"/>;
    /// overridable from tests so a structural error class (which the typed model cannot serialise into
    /// existence by construction) can be exercised through the real <see cref="Validate"/> path.
    /// </summary>
    private readonly Func<PlatformConfig, IEnumerable<string>> _structuralErrors;

    /// <summary>
    /// Paths already reported, so the operator is told once rather than on every
    /// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> resolution (which re-runs this validator).
    /// </summary>
    private readonly HashSet<string> _warnedPaths = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the validator. The logger is optional so the non-DI call sites
    /// (<see cref="ResilientJsonConfigurationSource"/>, tests) keep working unchanged; when absent,
    /// survivable structural errors are still tolerated, just not reported.
    /// </summary>
    public PlatformConfigOptionsValidator(ILogger<PlatformConfigOptionsValidator>? logger = null)
        : this(logger, PlatformConfigSchema.ValidateObject)
    {
    }

    internal PlatformConfigOptionsValidator(
        ILogger<PlatformConfigOptionsValidator>? logger,
        Func<PlatformConfig, IEnumerable<string>> structuralErrors)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _structuralErrors = structuralErrors;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformConfig options)
    {
        var errors = new List<string>();
        errors.AddRange(_structuralErrors(options)
            .Where(error => !IsQuarantinableAgentError(error))
            .Where(error => !ReportIfSurvivable(error)));
        errors.AddRange(PlatformConfigLoader.ValidateAnnotated(options)
            .Where(error => !IsQuarantinableAgentError(error))
            .Where(error => !ReportIfSurvivable(error)));

        var distinctErrors = errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinctErrors.Length > 0
            ? ValidateOptionsResult.Fail(distinctErrors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="error"/> is survivable (and therefore
    /// must be filtered out of the fatal set), emitting exactly one warning per distinct offending
    /// property path on the way through. Returns <see langword="false"/> for fatal errors, which are
    /// left untouched and unlogged here.
    /// </summary>
    private bool ReportIfSurvivable(string error)
    {
        if (!IsSurvivableStructuralError(error))
            return false;

        var path = ExtractPath(error);
        lock (_warnedPaths)
        {
            if (!_warnedPaths.Add(path))
                return true;
        }

        _logger.LogWarning(
            "Configuration property '{ConfigurationPath}' is not modelled by this version of BotNexus and " +
            "has no effect. Startup continues because the key cannot change the bound configuration. " +
            "Original schema error: {SchemaError}",
            path,
            error);

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="error"/> is a purely <em>structural</em>
    /// schema error that cannot alter the bound <see cref="PlatformConfig"/>, and the gateway can
    /// therefore run with the object it already has.
    /// </summary>
    /// <remarks>
    /// <para>The survivability rule: an error is survivable when it describes a property the typed
    /// model does not bind at all. <c>NoAdditionalPropertiesAllowed</c> is the motivating (and
    /// currently only) such class - configuration binding ignores unmapped keys, so accepting or
    /// rejecting the key yields a byte-for-byte identical bound object and refusing to start buys
    /// nothing. Errors that describe a value which IS bound (unparseable enum, out-of-range number,
    /// missing required field, failed cross-field <see cref="PlatformConfig"/> rule) are NOT
    /// survivable: they change or invalidate the object the gateway will actually use.</para>
    /// <para>Errors scoped to the reserved <c>agents.defaults</c> pseudo-agent are excluded even when
    /// structural, preserving the existing rule that anything seeding every agent fails hard.</para>
    /// <para>New error classes fall through to <see langword="false"/> (fatal) by design, so widening
    /// the survivable set is always a deliberate, reviewable edit rather than an accident.</para>
    /// </remarks>
    internal static bool IsSurvivableStructuralError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        if (!error.Contains("NoAdditionalPropertiesAllowed", StringComparison.Ordinal))
            return false;

        // agents.defaults seeds every agent; its errors stay fatal even when structural.
        var scoped = error.StartsWith("schema.", StringComparison.OrdinalIgnoreCase)
            ? error["schema.".Length..]
            : error;

        return !scoped.StartsWith("agents.defaults.", StringComparison.OrdinalIgnoreCase)
            && !scoped.StartsWith("agents.defaults:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the offending configuration path out of a formatted validation error
    /// (<c>schema.&lt;path&gt;: &lt;kind&gt; (...)</c>), falling back to the whole message.
    /// </summary>
    private static string ExtractPath(string error)
    {
        var colon = error.IndexOf(':', StringComparison.Ordinal);
        var path = colon > 0 ? error[..colon] : error;
        return path.StartsWith("schema.", StringComparison.OrdinalIgnoreCase)
            ? path["schema.".Length..]
            : path;
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
