using BotNexus.Gateway.Abstractions.Text;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Portal-preview truncation for the Blazor client surfaces (tool descriptions, session debug
/// snippets, steering queue rows). This is now a thin delegation to
/// <see cref="GraphemeSafeTruncation"/>, the single product-wide boundary policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>#2924 - this used to be a weaker copy.</b> The previous implementation backed off exactly one
/// UTF-16 code unit when the cut landed on a lone high surrogate. That is the correctness floor and
/// nothing more: it still split ZWJ emoji sequences (leaving a dangling U+200D), regional-indicator
/// flag pairs and combining marks, which is precisely what #2883 was filed to stop. The duplicate
/// existed for a real reason - <c>BlazorClient.Core</c> must not reference <c>BotNexus.Domain</c>,
/// because every assembly it references is downloaded by the browser and that one drags
/// <c>Vogen.SharedTypes</c> into the payload (#2329, fenced by
/// <c>WasmPayloadDependencyArchitectureTests</c>).
/// </para>
/// <para>
/// The shared algorithm therefore lives in <c>BotNexus.Domain.Wire</c>, the zero-dependency
/// assembly already inside the sanctioned WASM closure, so unification adds nothing to the browser
/// download. Do not reintroduce a local boundary calculation here: the fence
/// <c>SurrogateSafeTruncationArchitectureTests</c> now scans <c>src/extensions</c> as well as
/// <c>src/gateway</c> and will fail the build.
/// </para>
/// <para>
/// This type is kept (rather than deleted in favour of direct calls) only because three portal call
/// sites and a test suite name it; it adds no behaviour of its own beyond the empty-string
/// null-coalescing that its callers rely on.
/// </para>
/// </remarks>
public static class SurrogateSafeText
{
    /// <summary>
    /// Returns a prefix of <paramref name="value"/> at most <paramref name="max"/> UTF-16 code units
    /// long, cut on a grapheme-cluster boundary so it can never end on a lone surrogate, a dangling
    /// zero-width joiner, a severed flag pair or an orphaned combining mark. Values already within
    /// the limit are returned unchanged; <see langword="null"/> becomes <see cref="string.Empty"/>.
    /// </summary>
    /// <param name="value">The source string to truncate. May be null.</param>
    /// <param name="max">The maximum number of UTF-16 code units to keep. Non-positive returns empty.</param>
    public static string SurrogateSafeTruncate(string? value, int max)
        => GraphemeSafeTruncation.Truncate(value, max) ?? string.Empty;
}
