using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Runs once at gateway startup — after agents have been loaded — and normalises
/// any agent config JSON files that are missing expected default blocks.
/// Currently normalises: the <c>heartbeat</c> key.
/// </summary>
/// <remarks>
/// This service is intentionally narrow: it only injects keys that are
/// absent from the file. Explicitly-set values are never overwritten.
/// It targets individual agent JSON files loaded by
/// <see cref="FileAgentConfigurationSource"/>, not <c>config.json</c>.
/// </remarks>
internal sealed class ConfigNormalisationHostedService(
    IEnumerable<IAgentConfigurationSource> sources,
    IFileSystem fileSystem,
    ILogger<ConfigNormalisationHostedService> logger) : IHostedService
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<ConfigNormalisationHostedService> _logger = logger;

    // Resolve the directory paths that the FileAgentConfigurationSource instances are watching.
    private readonly string[] _agentDirectories = sources
        .OfType<FileAgentConfigurationSource>()
        .Select(s => s.DirectoryPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var directory in _agentDirectories)
        {
            if (!_fileSystem.Directory.Exists(directory))
                continue;

            foreach (var filePath in _fileSystem.Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryNormaliseFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task TryNormaliseFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await _fileSystem.File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(rawJson, nodeOptions: NodeOptions);
            if (root is not JsonObject obj)
                return;

            var mutated = false;

            // Inject heartbeat if absent
            if (!obj.ContainsKey("heartbeat"))
            {
                obj["heartbeat"] = BuildDefaultHeartbeatBlock();
                mutated = true;
            }

            if (!mutated)
                return;

            var agentId = obj["agentId"]?.GetValue<string>() ?? _fileSystem.Path.GetFileNameWithoutExtension(filePath);
            var updatedJson = obj.ToJsonString(WriteOptions);

            // Write via temp file + atomic move
            var dirName = _fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
            var tempPath = _fileSystem.Path.Combine(dirName, _fileSystem.Path.GetFileName(filePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                await _fileSystem.File.WriteAllTextAsync(tempPath, updatedJson, cancellationToken).ConfigureAwait(false);
                _fileSystem.File.Move(tempPath, filePath, overwrite: true);
                _logger.LogInformation("Normalised heartbeat config for agent '{AgentId}'.", agentId);
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
            _logger.LogWarning(ex, "Failed to normalise agent config file '{FilePath}'.", filePath);
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
