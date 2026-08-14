using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Gateway.Configuration.Shadow;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Evaluates <see cref="ConfigStoreFeatures.Authoritative"/> directly from <c>config.json</c>, for the
/// startup read that runs before <c>IFeatureManager</c> exists (#3180).
///
/// <para>
/// <b>Reads the same key the feature manager would.</b> Microsoft.FeatureManagement binds from the
/// <c>FeatureManagement</c> configuration section, so this looks in exactly that section of exactly
/// that file. The startup answer and every later answer therefore come from one source of truth; a
/// bespoke key here would let the gateway start from the store and then serve every subsequent read
/// from the file, or vice versa.
/// </para>
///
/// <para>
/// <b>Fails closed, for the same reason its DI counterpart does.</b> A missing key, a malformed file,
/// an unreadable disk - all evaluate to "not authoritative", leaving <c>config.json</c> serving
/// configuration. Since this runs before logging is fully configured, a failure here is deliberately
/// silent rather than half-logged: the safe behaviour is what protects the platform, not the message.
/// </para>
/// </summary>
public sealed class StartupFlagAuthoritativeGate(IFileSystem fileSystem, string configPath)
    : IConfigStoreAuthoritativeGate
{
    /// <inheritdoc />
    public Task<bool> IsAuthoritativeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Evaluate());

    private bool Evaluate()
    {
        try
        {
            if (!fileSystem.File.Exists(configPath))
                return false;

            var raw = fileSystem.File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("FeatureManagement", out var features))
                return false;

            if (!features.TryGetProperty(ConfigStoreFeatures.Authoritative, out var flag))
                return false;

            return flag.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fail closed: the file keeps serving configuration.
            return false;
        }
    }
}
