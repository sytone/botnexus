using System.Text.Json.Nodes;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that reports outstanding <see cref="IConfigCheck"/> config migrations without
/// mutating anything. It reuses the exact same registered <see cref="IConfigCheck"/> set that
/// <c>doctor config</c> applies, so the read-only health assessment can never diverge from the guided
/// migration (issue #2041: refactor existing DoctorConfig logic behind the check contract rather than
/// duplicate it). Applying fixes stays the job of <c>doctor config</c>; here we only surface the gaps.
/// </summary>
internal sealed class ConfigHealthCheck : IDoctorCheck
{
    public string Id => "config";
    public string Title => "Configuration health";

    public async Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(context.ConfigPath))
        {
            return DoctorCheckResult.Error(
                "config.json not found",
                $"Expected at {context.ConfigPath}. Run 'botnexus init' first.");
        }

        JsonObject root;
        try
        {
            var rawJson = await File.ReadAllTextAsync(context.ConfigPath, cancellationToken);
            root = JsonNode.Parse(rawJson)?.AsObject() ?? new JsonObject();
        }
        catch (Exception ex)
        {
            return DoctorCheckResult.Error("config.json could not be parsed", ex.Message);
        }

        var applicable = DoctorConfigCommand.Checks.Where(c => c.IsApplicable(root)).ToList();
        if (applicable.Count == 0)
            return DoctorCheckResult.Healthy("config is up to date - no migrations pending");

        var details = new List<string> { "Run 'botnexus doctor config' to review and apply:" };
        details.AddRange(applicable.Select(c => $"  - {c.Id}: {c.Description}"));
        return new DoctorCheckResult(
            DoctorOutcome.Warning,
            $"{applicable.Count} config migration(s) pending",
            details);
    }
}
