using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Diagnostics.Checkups.Security;

public sealed class ExtensionSignedCheckup(IOptions<BotNexusConfig> options) : IHealthCheckup
{
    private readonly BotNexusConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Name => "ExtensionSigned";
    public string Category => "Security";
    public string Description => "Checks loaded extension assemblies are strong-name signed when required.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_config.Extensions.RequireSignedAssemblies)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Pass,
                    "Signed extension assemblies are not required by configuration."));
            }

            var extensionRoot = BotNexusHome.ResolvePath(_config.ExtensionsPath);
            var loadedExtensions = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
                .Where(assembly => assembly.Location.StartsWith(extensionRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (loadedExtensions.Count == 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    "No loaded extension assemblies were found to validate signatures.",
                    "Load extensions and re-run diagnostics to validate strong-name signatures."));
            }

            var unsigned = loadedExtensions
                .Where(assembly =>
                {
                    var token = assembly.GetName().GetPublicKeyToken();
                    return token is null || token.Length == 0;
                })
                .Select(assembly => assembly.GetName().Name ?? assembly.FullName ?? "<unknown>")
                .ToList();

            if (unsigned.Count > 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Unsigned loaded extension assemblies: {string.Join(", ", unsigned)}",
                    "Strong-name sign extension assemblies or disable RequireSignedAssemblies for development-only scenarios."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                $"All {loadedExtensions.Count} loaded extension assembly(ies) are strong-name signed."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to validate extension signatures: {ex.Message}",
                "Inspect extension assembly metadata and signing configuration."));
        }
    }
}
