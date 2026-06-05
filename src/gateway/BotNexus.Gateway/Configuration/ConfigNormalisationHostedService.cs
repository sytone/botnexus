using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Runs once at gateway startup and normalises <c>config.json</c> to ensure
/// expected default blocks are present.
/// Currently normalises: injects a default <c>heartbeat</c> block into
/// <c>agents.defaults</c> if the key is absent.
/// </summary>
/// <remarks>
/// This service targets the single root <c>config.json</c> — the only agent
/// configuration file in BotNexus. It never overwrites explicitly-set values.
/// </remarks>
internal sealed class ConfigNormalisationHostedService(
    IFileSystem fileSystem,
    ILogger<ConfigNormalisationHostedService> logger) : IHostedService
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<ConfigNormalisationHostedService> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configPath = PlatformConfigLoader.GetDefaultConfigPath(_fileSystem);
        await TryNormaliseConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task TryNormaliseConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(configPath))
            return;

        try
        {
            var rawJson = await _fileSystem.File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(rawJson, nodeOptions: NodeOptions);
            if (root is not JsonObject obj)
                return;

            var mutated = false;

            // Ensure agents.defaults.heartbeat is present
            var agents = obj["agents"] as JsonObject;
            if (agents is not null)
            {
                var defaults = agents["defaults"] as JsonObject;
                if (defaults is null)
                {
                    defaults = new JsonObject(NodeOptions);
                    agents["defaults"] = defaults;
                }

                if (!defaults.ContainsKey("heartbeat"))
                {
                    defaults["heartbeat"] = BuildDefaultHeartbeatBlock();
                    mutated = true;
                    _logger.LogInformation("Injected default heartbeat block into agents.defaults in '{ConfigPath}'.", configPath);
                }
            }

            if (!mutated)
                return;

            var updatedJson = obj.ToJsonString(WriteOptions);

            // Write via temp file + atomic move to avoid partial-write corruption
            var dirName = _fileSystem.Path.GetDirectoryName(configPath) ?? string.Empty;
            var tempPath = _fileSystem.Path.Combine(
                dirName,
                _fileSystem.Path.GetFileName(configPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                await _fileSystem.File.WriteAllTextAsync(tempPath, updatedJson, cancellationToken).ConfigureAwait(false);
                _fileSystem.File.Move(tempPath, configPath, overwrite: true);
            }
            catch
            {
                if (_fileSystem.File.Exists(tempPath))
                    _fileSystem.File.Delete(tempPath);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to normalise config file '{ConfigPath}'.", configPath);
        }
    }

    private static JsonObject BuildDefaultHeartbeatBlock() => new()
    {
        ["enabled"] = true,
        ["intervalMinutes"] = 30,
        ["quietHours"] = new JsonObject
        {
            ["enabled"] = true,
            ["start"] = "23:00",
            ["end"] = "07:00"
        }
    };
}
