using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that probes every registered <see cref="Location"/> for accessibility using
/// the shared <see cref="LocationProbe"/>. It mirrors what <c>doctor locations</c> renders in detail,
/// but folds the per-location results into a single section outcome for the aggregate report
/// (issue #2041). A missing target is an error, an unreachable-but-optional endpoint is a warning.
/// </summary>
internal sealed class LocationAccessibilityCheck : IDoctorCheck
{
    public string Id => "locations";
    public string Title => "Location accessibility";

    public async Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(context.ConfigPath))
        {
            return DoctorCheckResult.Error(
                "config.json not found",
                $"Expected at {context.ConfigPath}. Run 'botnexus init' first.");
        }

        PlatformConfig config;
        try
        {
            config = await PlatformConfigLoader.LoadAsync(context.ConfigPath, cancellationToken, validateOnLoad: false);
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error("config.json could not be loaded", ex.Message);
        }

        var locations = WorldDescriptorBuilder.Build(config, null, null)
            .Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (locations.Length == 0)
            return DoctorCheckResult.Healthy("no locations registered");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var healthy = 0;
        var warning = 0;
        var error = 0;
        var details = new List<string>();

        foreach (var location in locations)
        {
            var result = await LocationProbe.CheckLocationAsync(location, httpClient, cancellationToken);
            switch (result.Status)
            {
                case LocationHealthStatus.Healthy:
                    healthy++;
                    if (context.Verbose)
                        details.Add($"  [ok]   {location.Name}: {result.Message}");
                    break;
                case LocationHealthStatus.Warning:
                    warning++;
                    details.Add($"  [warn] {location.Name}: {result.Message} ({result.Target})");
                    break;
                default:
                    error++;
                    details.Add($"  [err]  {location.Name}: {result.Message} ({result.Target})");
                    break;
            }
        }

        var summary = $"{locations.Length} location(s): {healthy} healthy, {warning} warning, {error} error";
        var outcome = error > 0 ? DoctorOutcome.Error
            : warning > 0 ? DoctorOutcome.Warning
            : DoctorOutcome.Healthy;
        return new DoctorCheckResult(outcome, summary, details);
    }
}
