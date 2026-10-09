using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Copilot.Headers;

/// <summary>Request-start attribution captured independently of Activity listeners or response bodies.</summary>
public sealed class CopilotHeaderCapture
{
    private static long _requestOrder;
    private readonly ICopilotHeaderSink? _sink;
    private readonly CopilotHeaderScope? _scope;
    private readonly bool _legacy;
    private readonly long _order;
    private long _observationOrder;
    private static readonly (string Header, CopilotQuotaDimension Dimension)[] Headers =
    [
        ("x-quota-snapshot-chat", CopilotQuotaDimension.Chat),
        ("x-quota-snapshot-completions", CopilotQuotaDimension.Completions),
        ("x-quota-snapshot-premium_interactions", CopilotQuotaDimension.PremiumInteractions)
    ];

    private CopilotHeaderCapture(ICopilotHeaderSink? sink, CopilotHeaderScope? scope, bool legacy)
    {
        _sink = sink;
        _scope = scope;
        _legacy = legacy;
        _order = Interlocked.Increment(ref _requestOrder);
    }

    /// <summary>Captures verified host attribution once for a logical request, retaining it across retries and transport fallback.</summary>
    public static CopilotHeaderCapture Begin(ICopilotHeaderSink? sink, LlmModel model, StreamOptions? options)
    {
        var hasScope = options?.Metadata?.ContainsKey(CopilotHeaderScope.MetadataKey) == true;
        var scope = hasScope && options?.Metadata?[CopilotHeaderScope.MetadataKey] is CopilotHeaderScope typed &&
            typed.Instance == CopilotHeaderScope.NormalizeInstance(model.Provider) &&
            !HasCredentialOverride(model.Headers) && !HasCredentialOverride(options?.Headers) ? typed : null;
        return new(sink, scope, !hasScope);
    }

    private static bool HasCredentialOverride(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("api-key", StringComparison.OrdinalIgnoreCase) || key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Observes only allowlisted response headers before body access. Null and all observation faults are harmless.</summary>
    public void Observe(HttpResponseMessage? response)
    {
        if (_sink is null || response is null) return;
        var ordinal = Interlocked.Increment(ref _observationOrder);
        foreach (var (header, dimension) in Headers)
        {
            Guard(() =>
            {
                if (response.Headers.TryGetValues(header, out var values)) ObserveOne(values, dimension, ordinal);
                else if (response.Content?.Headers.TryGetValues(header, out var contentValues) == true) ObserveOne(contentValues, dimension, ordinal);
            });
        }
        if (_legacy) NotifyLegacy();
    }

    /// <summary>Observes handshake headers without allowing access, enumeration, parsing, disposal, or sink faults to affect model execution.</summary>
    public void Observe(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        if (_sink is null || headers is null) return;
        var ordinal = Interlocked.Increment(ref _observationOrder);
        foreach (var (header, dimension) in Headers)
        {
            Guard(() =>
            {
                // ClientWebSocket uses case-insensitive keys; adapters need not.
                var values = headers.FirstOrDefault(pair => string.Equals(pair.Key, header, StringComparison.OrdinalIgnoreCase)).Value;
                if (values is not null) ObserveOne(values, dimension, ordinal);
            });
        }
        if (_legacy) NotifyLegacy();
    }

    /// <summary>Evaluates a transport header property inside the observational guard, preserving the original handshake exception.</summary>
    public void Observe(Func<IReadOnlyDictionary<string, IEnumerable<string>>?> getHeaders)
    {
        if (_sink is null) return;
        Guard(() => Observe(getHeaders()));
    }

    private void ObserveOne(IEnumerable<string> values, CopilotQuotaDimension dimension, long ordinal)
    {
        if (_scope is null || _sink is null) return;
        CopilotHeaderQuota quota;
        // Dispose before publishing; a faulty enumerator cannot publish a partially read snapshot.
        using (var iterator = values.GetEnumerator())
        {
            if (!iterator.MoveNext()) return;
            var first = iterator.Current;
            quota = iterator.MoveNext() ? CopilotHeaderQuota.Unknown : CopilotHeaderQuotaParser.Parse(first);
        }
        _sink.Observe(new(_scope, dimension, _order, DateTimeOffset.UtcNow, quota, ordinal));
    }

    private void NotifyLegacy() => Guard(() => _sink?.ObserveLegacyUnattributed());

    private static void Guard(Action observe)
    {
        try { observe(); }
        catch (Exception) { /* Observational metadata must never alter provider control flow. */ }
    }
}
