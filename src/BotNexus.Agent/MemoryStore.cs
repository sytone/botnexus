using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent;

/// <summary>
/// Reads and writes memory files for agents.
/// Memory is stored as plain text files in the agent's workspace.
/// </summary>
public sealed class MemoryStore : IMemoryStore
{
    private readonly string _legacyBasePath;
    private readonly ILogger<MemoryStore> _logger;

    public MemoryStore(string basePath, ILogger<MemoryStore> logger)
    {
        _legacyBasePath = basePath;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> ReadAsync(string agentName, string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        var legacyPath = GetLegacyPath(agentName, key);
        if (File.Exists(legacyPath))
            return await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);

        if (!key.Equals("MEMORY", StringComparison.OrdinalIgnoreCase))
        {
            var legacyMarkdownPath = GetLegacyPath(agentName, key, ".md");
            if (File.Exists(legacyMarkdownPath))
                return await File.ReadAllTextAsync(legacyMarkdownPath, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string agentName, string key, string content, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Memory written: {AgentName}/{Key}", agentName, key);
    }

    /// <inheritdoc/>
    public async Task AppendAsync(string agentName, string key, string content, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string agentName, string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListKeysAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeys(keys, Path.Combine(BotNexusHome.GetAgentWorkspacePath(agentName), "memory"));
        AddKeys(keys, Path.Combine(_legacyBasePath, agentName, "memory"));

        if (File.Exists(Path.Combine(BotNexusHome.GetAgentWorkspacePath(agentName), "MEMORY.md")) ||
            File.Exists(GetLegacyPath(agentName, "MEMORY")) ||
            File.Exists(GetLegacyPath(agentName, "MEMORY", ".md")))
        {
            keys.Add("MEMORY");
        }

        return Task.FromResult<IReadOnlyList<string>>(keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void AddKeys(HashSet<string> keys, string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(directory, file);
            var normalized = relative.Replace('\\', '/');
            var key = normalized[..^extension.Length];
            keys.Add(key);
        }
    }

    private static string NormalizeKey(string key)
        => key.Replace('\\', '/').Trim('/');

    private static string[] GetPathSegments(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string GetPath(string agentName, string key)
    {
        var workspacePath = BotNexusHome.GetAgentWorkspacePath(agentName);
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey.Equals("MEMORY", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(workspacePath, "MEMORY.md");

        var keyWithExtension = $"{normalizedKey}.md";
        return Path.Combine([workspacePath, "memory", .. GetPathSegments(keyWithExtension)]);
    }

    private string GetLegacyPath(string agentName, string key, string extension = ".txt")
    {
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey.Equals("MEMORY", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_legacyBasePath, agentName, "memory", $"MEMORY{extension}");

        var keyWithExtension = $"{normalizedKey}{extension}";
        return Path.Combine([_legacyBasePath, agentName, "memory", .. GetPathSegments(keyWithExtension)]);
    }
}
