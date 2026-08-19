using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;

namespace BotNexus.Memory.Tools;

/// <summary>
/// Appends markdown notes to an agent's memory via the <see cref="IAgentMemory"/> abstraction.
/// Supports both the default daily note target, explicit file paths, and shared stores.
/// </summary>
public sealed class MemorySaveTool : IAgentTool
{
    private readonly IAgentMemory _agentMemory;
    private readonly string _agentId;
    private readonly ISharedMemoryStoreRegistry? _sharedRegistry;
    private readonly IMemoryEnablementProvider? _enablement;

    public MemorySaveTool(IAgentMemory agentMemory, string agentId, ISharedMemoryStoreRegistry? sharedRegistry = null, IMemoryEnablementProvider? enablement = null)
    {
        _agentMemory = agentMemory ?? throw new ArgumentNullException(nameof(agentMemory));
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
        _sharedRegistry = sharedRegistry;
        _enablement = enablement;
    }

    public string Name => "memory_save";

    public string Label => "Memory Save";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Returns only a locally generated save confirmation.</summary>
    public string ContentSource => ToolContentSource.Local;

    public Tool Definition => new(
        Name,
        "Append markdown memory notes. Use content only for today's daily note, or provide file_path for a specific note under memory root.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "content": {
                  "type": "string",
                  "description": "Content to append to the memory note"
                },
                "file_path": {
                  "type": "string",
                  "description": "Optional relative note path under memory root"
                },
                "store": {
                  "type": "string",
                  "description": "Optional shared store name to save to. When set, content is saved to the named shared store instead of the agent's own memory."
                },
                "category": {
                  "type": "string",
                  "description": "Optional category for classification: decision, pattern, fact, procedure, or preference.",
                  "enum": ["decision", "pattern", "fact", "procedure", "preference"]
                },
                "tags": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional tags for classification and filtering."
                }
              },
              "required": ["content"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("content", out var contentValue) || string.IsNullOrWhiteSpace(ToStringValue(contentValue)))
            throw new ArgumentException("Missing required argument: content.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = ToStringValue(contentValue)!
        };

        if (arguments.TryGetValue("file_path", out var filePathValue) && !string.IsNullOrWhiteSpace(ToStringValue(filePathValue)))
            prepared["file_path"] = ToStringValue(filePathValue);

        if (arguments.TryGetValue("store", out var storeValue) && !string.IsNullOrWhiteSpace(ToStringValue(storeValue)))
            prepared["store"] = ToStringValue(storeValue);

        if (arguments.TryGetValue("category", out var categoryValue) && !string.IsNullOrWhiteSpace(ToStringValue(categoryValue)))
            prepared["category"] = ToStringValue(categoryValue);

        if (arguments.TryGetValue("tags", out var tagsValue) && tagsValue is not null)
            prepared["tags"] = tagsValue;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // #3361: live revocation, evaluated before argument coercion, quarantine evaluation and any
        // write path branch - so a disabled agent makes zero calls into IAgentMemory or any shared
        // store, and no partial side effect (a quarantine tag, a shared-store insert) can escape.
        if (MemoryEnablementGate.Refuse(_enablement) is { } refusal)
            return refusal;

        var content = ToStringValue(arguments["content"])!;
        var filePath = arguments.TryGetValue("file_path", out var filePathValue)
            ? ToStringValue(filePathValue)
            : null;
        var storeName = arguments.TryGetValue("store", out var storeValue)
            ? ToStringValue(storeValue)
            : null;
        var category = arguments.TryGetValue("category", out var categoryValue)
            ? ToStringValue(categoryValue)
            : null;
        var tags = ParseTags(arguments.TryGetValue("tags", out var tagsValue) ? tagsValue : null);

        // #2519: evaluate the ambient run taint ONCE, before any write path branches, so every
        // target (file note, agent memory, shared store) applies the identical decision. Evaluating
        // per-branch is how one path silently keeps laundering after the others are fixed.
        var quarantine = MemoryQuarantine.Evaluate();
        content = quarantine.ApplyTo(content);
        if (quarantine.IsQuarantined)
        {
            // Tags carry the marker too, so a tag-filtered recall cannot select quarantined
            // entries while dropping the signal that they are quarantined.
            tags = AppendQuarantineTag(tags);
        }

        // If saving to a shared store, validate access and write there
        if (!string.IsNullOrWhiteSpace(storeName))
        {
            return await SaveToSharedStoreAsync(content, storeName!, category, tags, quarantine, cancellationToken).ConfigureAwait(false);
        }

        // Standard agent-local save
        if (!string.IsNullOrWhiteSpace(filePath) && _agentMemory is MarkdownAgentMemory markdownMemory)
        {
            await markdownMemory.SaveToFileAsync(content, filePath, cancellationToken);
        }
        else
        {
            var request = new AgentMemorySaveRequest(
                AgentId: _agentId,
                Content: content,
                SourceType: "tool",
                Tags: CombineTags(category, tags));
            await _agentMemory.SaveAsync(request, cancellationToken);
        }

        var targetDescription = string.IsNullOrWhiteSpace(filePath)
            ? "default memory target"
            : filePath;

        // The confirmation states the quarantine explicitly. The model must not be able to believe
        // it recorded first-party knowledge when it did not - a silent downgrade would leave it
        // reasoning on a false premise about its own store.
        var confirmation = quarantine.IsQuarantined
            ? $"Appended memory note to {targetDescription}, QUARANTINED as untrusted-origin " +
              $"because this run consumed content from {quarantine.ContributorSummary}. It will be " +
              "recalled with an untrusted-origin marker and is not first-party knowledge."
            : $"Appended memory note to {targetDescription}.";
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, confirmation)]);
    }

    /// <summary>
    /// Adds the quarantine tag without disturbing caller-supplied tags, and without duplicating it
    /// if the caller (or a re-entrant path) already supplied it.
    /// </summary>
    private static IReadOnlyList<string> AppendQuarantineTag(IReadOnlyList<string>? tags)
    {
        const string quarantineTag = "untrusted-origin";
        var combined = new List<string>(tags ?? []);
        if (!combined.Any(tag => string.Equals(tag, quarantineTag, StringComparison.OrdinalIgnoreCase)))
            combined.Add(quarantineTag);
        return combined;
    }

    private async Task<AgentToolResult> SaveToSharedStoreAsync(
        string content, string storeName, string? category,
        IReadOnlyList<string>? tags, MemoryQuarantineDecision quarantine,
        CancellationToken cancellationToken)
    {
        if (_sharedRegistry is null)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "Shared memory stores are not configured.")]);

        if (!_sharedRegistry.CanWrite(_agentId, storeName))
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Access denied: agent '{_agentId}' cannot write to store '{storeName}'.")]);

        var store = _sharedRegistry.GetStore(storeName);
        if (store is null)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Store '{storeName}' not found.")]);

        var entry = new MemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Content = content,
            SourceType = category ?? "tool",
            AgentId = _agentId,
            CreatedAt = DateTimeOffset.UtcNow,
            // The agent invoked memory_save deliberately, so the content is its own (#2480) - but
            // only when the run that produced it was clean. A tainted run downgrades this to
            // external-untrusted (#2519), which MemoryProvenance.IsFirstParty already rejects.
            Provenance = quarantine.Provenance,
            MetadataJson = BuildMetadataJson(category, tags)
        };

        await store.InsertAsync(entry, cancellationToken).ConfigureAwait(false);
        var sharedConfirmation = quarantine.IsQuarantined
            ? $"Saved memory note to shared store '{storeName}', QUARANTINED as untrusted-origin " +
              $"because this run consumed content from {quarantine.ContributorSummary}."
            : $"Saved memory note to shared store '{storeName}'.";
        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, sharedConfirmation)]);
    }

    private static IReadOnlyList<string>? CombineTags(string? category, IReadOnlyList<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(category) && (tags is null || tags.Count == 0))
            return null;

        var combined = new List<string>();
        if (!string.IsNullOrWhiteSpace(category))
            combined.Add($"category:{category}");
        if (tags is not null)
            combined.AddRange(tags);
        return combined;
    }

    private static string? BuildMetadataJson(string? category, IReadOnlyList<string>? tags)
    {
        var combinedTags = CombineTags(category, tags);
        if (combinedTags is null || combinedTags.Count == 0)
            return null;
        return JsonSerializer.Serialize(new { tags = combinedTags });
    }

    private static IReadOnlyList<string>? ParseTags(object? value)
    {
        if (value is null) return null;

        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            var result = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    result.Add(item.GetString()!);
            }
            return result.Count > 0 ? result : null;
        }

        if (value is IEnumerable<string> strings)
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        return null;
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
}
