using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Validates <see cref="PlatformConfig"/> through the options pipeline on startup,
/// providing fast-fail behavior for misconfigured gateways.
/// Delegates to the existing BotNexus domain validation helpers.
/// </summary>
public sealed class PlatformConfigOptionsValidator : IValidateOptions<PlatformConfig>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlatformConfig options)
    {
        var errors = new List<string>();
        errors.AddRange(PlatformConfigSchema.ValidateObject(options));
        errors.AddRange(PlatformConfigLoader.Validate(options));

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
