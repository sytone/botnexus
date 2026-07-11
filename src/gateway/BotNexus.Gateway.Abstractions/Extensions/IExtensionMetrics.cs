using System.Diagnostics.Metrics;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>
/// The sanctioned metrics seam for extensions (#1852). An extension resolves this from DI instead
/// of the raw platform <c>IMetrics</c> facade so that every instrument it creates is automatically
/// prefixed into, and validated against, its own <c>botnexus.ext.&lt;id&gt;.*</c> namespace. This is
/// the same underlying <see cref="Meter"/> the platform core emits on - there is no privileged
/// internal-only path - but the guardrails guarantee an extension cannot mint an instrument that
/// collides with a platform instrument (e.g. <c>botnexus.turns.*</c>).
/// </summary>
/// <remarks>
/// Callers pass only the unprefixed leaf name (e.g. <c>loads</c>); the implementation prepends
/// <c>botnexus.ext.&lt;id&gt;.</c>. A leaf that itself starts with <c>botnexus.</c> is rejected so an
/// extension cannot smuggle a platform-shaped name through.
/// </remarks>
public interface IExtensionMetrics
{
    /// <summary>The owning extension id; the instrument namespace segment for every instrument.</summary>
    string ExtensionId { get; }

    /// <summary>Creates a monotonically increasing counter under <c>botnexus.ext.&lt;id&gt;.&lt;name&gt;</c>.</summary>
    Counter<T> CreateCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>Creates a histogram under <c>botnexus.ext.&lt;id&gt;.&lt;name&gt;</c>.</summary>
    Histogram<T> CreateHistogram<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>Creates an up/down counter under <c>botnexus.ext.&lt;id&gt;.&lt;name&gt;</c>.</summary>
    UpDownCounter<T> CreateUpDownCounter<T>(string name, string? unit = null, string? description = null)
        where T : struct;

    /// <summary>Registers an observable gauge under <c>botnexus.ext.&lt;id&gt;.&lt;name&gt;</c>.</summary>
    ObservableGauge<T> CreateObservableGauge<T>(
        string name,
        Func<T> observeValue,
        string? unit = null,
        string? description = null)
        where T : struct;
}
