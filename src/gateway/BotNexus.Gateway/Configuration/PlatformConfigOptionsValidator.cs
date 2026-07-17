using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Validates <see cref="PlatformConfig"/> through the options pipeline on startup,
/// providing fast-fail behavior for misconfigured gateways.
/// </summary>
/// <remarks>
/// Server-side validation is unified on the annotated model (#1613, config parity PBI 5/6 of
/// #1579): the per-field DataAnnotations and the cross-field <c>IValidatableObject</c> escape
/// hatch are both enforced by <see cref="PlatformConfigLoader.ValidateAnnotated"/>, which runs
/// <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/>. The structural
/// JSON-schema check (<see cref="PlatformConfigSchema.ValidateObject"/>) is retained alongside it
/// to catch shape errors the typed model cannot express; the same DataAnnotations now also appear
/// in that generated schema, so the rules are readable client-side as well.
/// </remarks>
public sealed class PlatformConfigOptionsValidator : IValidateOptions<PlatformConfig>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformConfig options)
    {
        var errors = new List<string>();
        errors.AddRange(PlatformConfigSchema.ValidateObject(options)
            .Where(error => !IsQuarantinableAgentValueError(error)));
        errors.AddRange(PlatformConfigLoader.ValidateAnnotated(options));

        var distinctErrors = errors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinctErrors.Length > 0
            ? ValidateOptionsResult.Fail(distinctErrors)
            : ValidateOptionsResult.Success;
    }

    private static bool IsQuarantinableAgentValueError(string error)
        => error.StartsWith("schema.agents.", StringComparison.OrdinalIgnoreCase)
            && error.Contains(".thinking", StringComparison.OrdinalIgnoreCase);
}
