using System.Text.Json.Nodes;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Small readability helpers for asserting on <see cref="JsonObject"/> shape.
/// </summary>
/// <remarks>
/// <see cref="JsonObject"/> enumerates as key/value pairs but exposes no <c>Keys</c> collection,
/// so every "which entries exist on disk?" assertion would otherwise repeat the same projection.
/// </remarks>
internal static class JsonObjectExtensions
{
    /// <summary>Property names of <paramref name="obj"/> in ordinal order, for set assertions.</summary>
    internal static IReadOnlyList<string> KeyNames(this JsonObject obj)
        => [.. obj.Select(kvp => kvp.Key).Order(StringComparer.Ordinal)];
}
