using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Diagnostics.Checkups.Configuration;

public sealed class AgentConfigCheckup(IOptions<BotNexusConfig> options) : IHealthCheckup
{
    private readonly BotNexusConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Name => "AgentConfig";
    public string Category => "Configuration";
    public string Description => "Validates required fields for named agents.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var namedAgents = _config.Agents.Named;
            if (namedAgents.Count == 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    "No named agents are configured.",
                    "Add BotNexus:Agents:Named entries with Name and Provider fields."));
            }

            var missingFields = new List<string>();
            foreach (var (key, agent) in namedAgents)
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(agent.Name))
                    missing.Add("Name");
                if (string.IsNullOrWhiteSpace(agent.Provider))
                    missing.Add("Provider");

                if (missing.Count > 0)
                    missingFields.Add($"{key} missing [{string.Join(", ", missing)}]");
            }

            if (missingFields.Count > 0)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Invalid named agent configuration: {string.Join("; ", missingFields)}",
                    "Set BotNexus:Agents:Named:<agent>:Name and Provider for every named agent."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                $"Validated {namedAgents.Count} named agent configuration(s)."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to validate agent configuration: {ex.Message}",
                "Inspect BotNexus:Agents:Named values and ensure required fields are present."));
        }
    }
}
