using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotNexus.Agent.Providers.Copilot.Messages;

/// <summary>
/// Keeps account-specific Copilot Messages effort capabilities private to this provider. The
/// bounded cache learns only from the exact upstream rejection contract and never mutates global
/// model discovery state.
/// </summary>
internal sealed partial class CopilotEffortCapabilityCache
{
    private const int DefaultCapacity = 128;
    private static readonly string[] OrderedEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly HashSet<string> KnownEfforts = new(OrderedEfforts, StringComparer.Ordinal);

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, string[]> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _insertionOrder = new();

    internal CopilotEffortCapabilityCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    internal void Remember(string authoritativeModelId, string requestedAlias, IReadOnlyCollection<string> supported)
    {
        var accepted = supported.Where(KnownEfforts.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (accepted.Length == 0)
            return;

        lock (_gate)
        {
            Set(authoritativeModelId, accepted);
            if (!string.Equals(authoritativeModelId, requestedAlias, StringComparison.OrdinalIgnoreCase))
                Set(requestedAlias, accepted);
        }
    }

    internal string Clamp(string authoritativeModelId, string requestedAlias, string requested)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(authoritativeModelId, out var supported) ||
                _entries.TryGetValue(requestedAlias, out supported))
            {
                return SelectClosest(requested, supported) ?? requested;
            }
        }

        return requested;
    }

    internal static string? SelectClosest(string requested, IEnumerable<string> supported)
    {
        var supportedSet = supported.Where(KnownEfforts.Contains).ToHashSet(StringComparer.Ordinal);
        var requestedIndex = Array.IndexOf(OrderedEfforts, requested);
        if (requestedIndex < 0 || supportedSet.Count == 0)
            return null;
        if (supportedSet.Contains(requested))
            return requested;

        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (supportedSet.Contains(OrderedEfforts[i]))
                return OrderedEfforts[i];
        }

        for (var i = requestedIndex + 1; i < OrderedEfforts.Length; i++)
        {
            if (supportedSet.Contains(OrderedEfforts[i]))
                return OrderedEfforts[i];
        }

        return null;
    }

    internal static bool TryParseRejection(
        string body,
        string requested,
        out string authoritativeModelId,
        out string[] supported)
    {
        authoritativeModelId = string.Empty;
        supported = [];
        var message = UnwrapMessage(body);
        if (message is null)
            return false;

        var match = RejectionPattern().Match(message);
        if (!match.Success || !string.Equals(match.Groups["requested"].Value, requested, StringComparison.Ordinal))
            return false;

        supported = SupportedValueSeparator().Split(match.Groups["supported"].Value)
            .Where(value => value.Length > 0)
            .Where(KnownEfforts.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (supported.Length == 0)
            return false;

        authoritativeModelId = match.Groups["model"].Value;
        return true;
    }

    private void Set(string key, string[] supported)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (!_entries.ContainsKey(key))
            _insertionOrder.Enqueue(key);
        _entries[key] = supported;

        while (_entries.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            _entries.Remove(oldest);
    }

    private static string? UnwrapMessage(string body)
    {
        var trimmed = body.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var nestedMessage) &&
                nestedMessage.ValueKind == JsonValueKind.String)
            {
                return nestedMessage.GetString();
            }

            return document.RootElement.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^output_config\\.effort \\\"(?<requested>[^\\\"]+)\\\" is not supported by model (?<model>[^;\\s]+); supported values: \\[\\s*(?<supported>[^]\\r\\n]+?)\\s*\\]$", RegexOptions.CultureInvariant)]
    private static partial Regex RejectionPattern();

    [GeneratedRegex("[,\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex SupportedValueSeparator();
}
