namespace BotNexus.Prompts;

public sealed class ToolNameRegistry
{
    private readonly Dictionary<string, string> _canonicalByNormalized = new(StringComparer.OrdinalIgnoreCase);

    public ToolNameRegistry(IEnumerable<string>? rawToolNames)
    {
        foreach (var tool in rawToolNames ?? [])
        {
            var trimmed = tool?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || _canonicalByNormalized.ContainsKey(trimmed))
            {
                continue;
            }

            _canonicalByNormalized[trimmed.ToLowerInvariant()] = trimmed;
        }
    }

    public IReadOnlySet<string> NormalizedTools => _canonicalByNormalized.Keys.ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<string> RawTools => _canonicalByNormalized.Values.ToList();

    public string Resolve(string normalizedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        return _canonicalByNormalized.TryGetValue(normalizedName, out var canonical) ? canonical : normalizedName;
    }

    public bool Contains(string normalizedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        return _canonicalByNormalized.ContainsKey(normalizedName);
    }
}