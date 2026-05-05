using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

public sealed class PlatformConfigAgentWriter : IAgentConfigurationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private readonly BotNexusHome _botNexusHome;
    private readonly IFileSystem _fileSystem;
    private readonly ConfigBackupService? _backup;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PlatformConfigAgentWriter(string configPath, BotNexusHome botNexusHome, IFileSystem fileSystem, ConfigBackupService? backup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(botNexusHome);

        _configPath = Path.GetFullPath(configPath);
        _botNexusHome = botNexusHome;
        _fileSystem = fileSystem;
        _backup = backup;
    }

    public async Task SaveAsync(AgentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.AgentId);

        _ = _botNexusHome.GetAgentDirectory(descriptor.AgentId);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken);
            var agents = EnsureAgentsObject(root);
            var isNew = agents[descriptor.AgentId] is null;
            var entry = GetOrCreateAgentEntry(agents, descriptor.AgentId);

            entry["provider"] = descriptor.ApiProvider;
            entry["model"] = descriptor.ModelId;
            entry["displayName"] = descriptor.DisplayName;
            entry["enabled"] = true;
            SetOptionalString(entry, "description", descriptor.Description);
            SetOptionalString(entry, "systemPromptFile", descriptor.SystemPromptFile);
            SetOptionalList(entry, "allowedModels", descriptor.AllowedModelIds);
            SetOptionalList(entry, "subAgents", descriptor.SubAgentIds);
            SetOptionalList(entry, "toolIds", descriptor.ToolIds);
            SetOptionalString(entry, "isolationStrategy", descriptor.IsolationStrategy);
            SetOptionalInt(entry, "maxConcurrentSessions", descriptor.MaxConcurrentSessions);
            SetOptionalObject(entry, "metadata", descriptor.Metadata);
            SetOptionalObject(entry, "isolationOptions", descriptor.IsolationOptions);
            SetOptionalNode(entry, "soul", descriptor.Soul);

            var saveReason = isNew
                ? $"before-agent-create-{descriptor.AgentId}"
                : $"before-agent-update-{descriptor.AgentId}";
            await WriteRootAtomicallyAsync(root, saveReason, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_fileSystem.File.Exists(_configPath))
                return;

            var root = await ReadRootAsync(cancellationToken);
            if (!TryGetAgentsObject(root, out var agents))
                return;

            if (!agents.Remove(agentId))
                return;

            await WriteRootAtomicallyAsync(root, $"before-agent-delete-{agentId}", cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void SetOptionalString(JsonObject target, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = value;
    }

    private static void SetOptionalList(JsonObject target, string propertyName, IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(values, JsonOptions);
    }

    private static void SetOptionalInt(JsonObject target, string propertyName, int value)
    {
        if (value <= 0)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = value;
    }

    private static void SetOptionalObject(JsonObject target, string propertyName, IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(values, JsonOptions);
    }

    private static void SetOptionalNode<T>(JsonObject target, string propertyName, T? value) where T : class
    {
        if (value is null)
        {
            target.Remove(propertyName);
            return;
        }

        target[propertyName] = JsonSerializer.SerializeToNode(value, JsonOptions);
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(_configPath))
            return new JsonObject();

        await using var stream = _fileSystem.FileStream.New(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? new JsonObject();
    }

    private async Task WriteRootAtomicallyAsync(JsonObject root, string reason, CancellationToken cancellationToken)
    {
        _backup?.Backup(_configPath, reason);
        _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

        var tempPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var payload = root.ToJsonString(JsonOptions);
            await _fileSystem.File.WriteAllTextAsync(tempPath, payload, cancellationToken);
            _fileSystem.File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (_fileSystem.File.Exists(tempPath))
                _fileSystem.File.Delete(tempPath);
        }
    }

    private static JsonObject EnsureAgentsObject(JsonObject root)
    {
        if (root["agents"] is JsonObject agents)
            return agents;

        var created = new JsonObject();
        root["agents"] = created;
        return created;
    }

    private static bool TryGetAgentsObject(JsonObject root, out JsonObject agents)
    {
        if (root["agents"] is JsonObject existing)
        {
            agents = existing;
            return true;
        }

        agents = null!;
        return false;
    }

    private static JsonObject GetOrCreateAgentEntry(JsonObject agents, string agentId)
    {
        if (agents[agentId] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        agents[agentId] = created;
        return created;
    }
}
