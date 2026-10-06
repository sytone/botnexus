using BotNexus.Gateway.Configuration;
using BotNexus.Cli.Commands.Doctor.Generated;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>Moves the retired gateway extension block into the canonical loader and agent defaults paths.</summary>
[DoctorCheck(Id = "legacy-gateway-extensions", Suite = DoctorSuite.Config, Order = -1)]
public sealed class LegacyGatewayExtensionsCheck : IConfigCheck
{
    /// <inheritdoc />
    public string Id => "legacy-gateway-extensions";

    /// <inheritdoc />
    public string Description => "Legacy gateway.extensions loader/default settings are present.";

    /// <inheritdoc />
    public string FixDescription => "Move loader settings to gateway.extensionLoader and defaults to agents.defaults.extensions.";

    /// <inheritdoc />
    public bool IsApplicable(ConfigDocument config) => LegacyGatewayExtensionsMigration.IsApplicable(config);

    /// <inheritdoc />
    public void Apply(ConfigDocument config)
    {
        var result = LegacyGatewayExtensionsMigration.Apply(config);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(" ", result.Errors));
    }
}
