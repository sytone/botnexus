using System.Globalization;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Models;
using BotNexus.Providers.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Agent;

public sealed class MemoryConsolidator : IMemoryConsolidator
{
    private const string SystemPrompt = "You are a memory consolidation agent. Review the daily notes and update the long-term memory.";
    private readonly IMemoryStore _memoryStore;
    private readonly IAgentWorkspaceFactory _workspaceFactory;
    private readonly ProviderRegistry _providerRegistry;
    private readonly BotNexusConfig _config;
    private readonly ILogger<MemoryConsolidator> _logger;
    private readonly bool _archiveProcessedDailyFiles;

    public MemoryConsolidator(
        IMemoryStore memoryStore,
        IAgentWorkspaceFactory workspaceFactory,
        ProviderRegistry providerRegistry,
        IOptions<BotNexusConfig> config,
        ILogger<MemoryConsolidator> logger,
        bool archiveProcessedDailyFiles = true)
    {
        _memoryStore = memoryStore;
        _workspaceFactory = workspaceFactory;
        _providerRegistry = providerRegistry;
        _config = config.Value;
        _logger = logger;
        _archiveProcessedDailyFiles = archiveProcessedDailyFiles;
    }

    public async Task<MemoryConsolidationResult> ConsolidateAsync(string agentName, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspace = _workspaceFactory.Create(agentName);
            await workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var dailyFiles = GetEligibleDailyFiles(workspace.WorkspacePath);

            if (dailyFiles.Count == 0)
            {
                _logger.LogInformation("Memory consolidation skipped for {AgentName}: no daily files older than 1 day", agentName);
                return new MemoryConsolidationResult(true, 0, 0);
            }

            var currentMemory = await _memoryStore.ReadAsync(agentName, "MEMORY", cancellationToken).ConfigureAwait(false) ?? string.Empty;
            var dailyContent = await ReadDailyFilesAsync(agentName, dailyFiles, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(dailyContent.Content))
            {
                _logger.LogInformation("Memory consolidation found only empty daily files for {AgentName}. Archiving files without LLM call.", agentName);
                await ArchiveOrDeleteProcessedFilesAsync(workspace.WorkspacePath, dailyFiles, cancellationToken).ConfigureAwait(false);
                return new MemoryConsolidationResult(true, dailyFiles.Count, 0);
            }

            var provider = ResolveProvider(agentName);
            var updatedMemory = await GenerateUpdatedMemoryAsync(provider, agentName, currentMemory, dailyContent.Content, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(updatedMemory))
            {
                _logger.LogWarning("LLM returned empty consolidation output for {AgentName}. Falling back to raw append.", agentName);
                updatedMemory = BuildFallbackMemory(currentMemory, dailyContent.Content);
            }

            await _memoryStore.WriteAsync(agentName, "MEMORY", updatedMemory, cancellationToken).ConfigureAwait(false);
            await ArchiveOrDeleteProcessedFilesAsync(workspace.WorkspacePath, dailyFiles, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Memory consolidation completed for {AgentName}. Processed {DailyFilesProcessed} daily files and {EntriesConsolidated} entries.",
                agentName,
                dailyFiles.Count,
                dailyContent.EntryCount);

            return new MemoryConsolidationResult(true, dailyFiles.Count, dailyContent.EntryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory consolidation failed for {AgentName}", agentName);
            return new MemoryConsolidationResult(false, 0, 0, ex.Message);
        }
    }

    private async Task<string> GenerateUpdatedMemoryAsync(
        ILlmProvider provider,
        string agentName,
        string currentMemory,
        string dailyContent,
        CancellationToken cancellationToken)
    {
        var model = ResolveConsolidationModel(agentName) ?? provider.DefaultModel;
        var request = new ChatRequest(
            [new ChatMessage("user", BuildUserPrompt(currentMemory, dailyContent))],
            new GenerationSettings
            {
                Model = model,
                MaxTokens = provider.Generation.MaxTokens,
                Temperature = provider.Generation.Temperature,
                ContextWindowTokens = provider.Generation.ContextWindowTokens,
                MaxToolIterations = provider.Generation.MaxToolIterations
            },
            SystemPrompt: SystemPrompt);

        try
        {
            var response = await provider.ChatAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Content?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM consolidation failed for {AgentName}; using fallback append mode", agentName);
            return BuildFallbackMemory(currentMemory, dailyContent);
        }
    }

    private string? ResolveConsolidationModel(string agentName)
    {
        if (_config.Agents.Named.TryGetValue(agentName, out var namedConfig) &&
            !string.IsNullOrWhiteSpace(namedConfig.ConsolidationModel))
        {
            return namedConfig.ConsolidationModel;
        }

        return null;
    }

    private ILlmProvider ResolveProvider(string agentName)
    {
        var consolidationModel = ResolveConsolidationModel(agentName);
        if (!string.IsNullOrWhiteSpace(consolidationModel))
        {
            var modelProvider = ResolveProviderFromModel(consolidationModel);
            if (modelProvider is not null)
                return modelProvider;
        }

        var defaultProvider = _providerRegistry.GetDefault();
        if (defaultProvider is not null)
            return defaultProvider;

        throw new InvalidOperationException($"No LLM providers are registered for agent '{agentName}'.");
    }

    private ILlmProvider? ResolveProviderFromModel(string model)
    {
        var separatorIndex = model.IndexOfAny([':', '/']);
        if (separatorIndex > 0)
        {
            var providerName = model[..separatorIndex];
            var byPrefix = _providerRegistry.Get(providerName);
            if (byPrefix is not null)
                return byPrefix;
        }

        foreach (var providerName in _providerRegistry.GetProviderNames())
        {
            var provider = _providerRegistry.Get(providerName);
            if (provider is not null &&
                string.Equals(provider.DefaultModel, model, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    private static string BuildUserPrompt(string currentMemory, string dailyContents)
        => $"Current long-term memory:\n{currentMemory}\n\nDaily notes to process:\n{dailyContents}";

    private static string BuildFallbackMemory(string currentMemory, string dailyContents)
    {
        var sb = new StringBuilder();
        sb.Append(currentMemory.TrimEnd());
        if (sb.Length > 0)
            sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"## Consolidation Fallback ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC)");
        sb.AppendLine();
        sb.Append(dailyContents.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static List<string> GetEligibleDailyFiles(string workspacePath)
    {
        var dailyDirectory = Path.Combine(workspacePath, "memory", "daily");
        if (!Directory.Exists(dailyDirectory))
            return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return Directory.EnumerateFiles(dailyDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                return DateOnly.TryParseExact(fileName, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate) &&
                       fileDate < today;
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<(string Content, int EntryCount)> ReadDailyFilesAsync(
        string agentName,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var entries = 0;
        foreach (var filePath in filePaths)
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var fileName = Path.GetFileName(filePath);
            sb.AppendLine($"### {agentName}/memory/daily/{fileName}");
            sb.AppendLine(content.Trim());
            sb.AppendLine();

            entries += content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Count(static line => !string.IsNullOrWhiteSpace(line));
        }

        return (sb.ToString().TrimEnd(), entries);
    }

    private Task ArchiveOrDeleteProcessedFilesAsync(string workspacePath, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return Task.CompletedTask;

        if (_archiveProcessedDailyFiles)
        {
            var archiveDirectory = Path.Combine(workspacePath, "memory", "daily", "archived");
            Directory.CreateDirectory(archiveDirectory);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(archiveDirectory, Path.GetFileName(file));
                if (File.Exists(target))
                    target = Path.Combine(archiveDirectory, $"{Path.GetFileNameWithoutExtension(file)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(file)}");
                File.Move(file, target);
            }

            _logger.LogInformation("Archived {Count} daily memory files to {ArchiveDirectory}", files.Count, archiveDirectory);
            return Task.CompletedTask;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        _logger.LogInformation("Deleted {Count} processed daily memory files", files.Count);
        return Task.CompletedTask;
    }
}
