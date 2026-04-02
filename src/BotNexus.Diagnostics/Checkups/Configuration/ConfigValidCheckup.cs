using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;

namespace BotNexus.Diagnostics.Checkups.Configuration;

public sealed class ConfigValidCheckup(DiagnosticsPaths paths) : IHealthCheckup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DiagnosticsPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public string Name => "ConfigValid";
    public string Category => "Configuration";
    public string Description => "Validates that config.json is valid JSON and binds to BotNexusConfig.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var configPath = _paths.ConfigPath;
            if (!File.Exists(configPath))
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    $"Config file not found at '{configPath}'.",
                    "Create ~/.botnexus/config.json or set BOTNEXUS_HOME to the correct location."));
            }

            var json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    $"Config file '{configPath}' is empty.",
                    "Populate config.json with a valid BotNexus configuration object."));
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            BotNexusConfig? config = null;
            if (root.TryGetProperty(BotNexusConfig.SectionName, out var section))
                config = section.Deserialize<BotNexusConfig>(JsonOptions);
            else if (root.ValueKind == JsonValueKind.Object)
                config = root.Deserialize<BotNexusConfig>(JsonOptions);

            if (config is null)
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Fail,
                    "config.json could not be bound to BotNexusConfig.",
                    "Ensure config.json contains a valid object either under BotNexus or at the root."));
            }

            return Task.FromResult(new CheckupResult(
                CheckupStatus.Pass,
                "Config file is valid JSON and binds to BotNexusConfig."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to validate config.json: {ex.Message}",
                "Fix JSON syntax and verify config fields match BotNexusConfig."));
        }
    }
}
