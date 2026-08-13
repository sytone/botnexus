using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// One addressed change in an atomic config save (issue #2059).
/// </summary>
/// <remarks>
/// Declared client-side rather than shared from the gateway API assembly on purpose: the Blazor
/// WASM payload must not take a dependency that drags server-side assemblies into the browser
/// bundle. The shape is the wire contract of <c>PATCH /api/config</c>; the server-side record is
/// its mirror and the round-trip is pinned by tests rather than by a shared type.
/// </remarks>
/// <remarks>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than left to a
/// serializer naming policy. <c>PlatformConfigService</c>'s options set only
/// <c>PropertyNameCaseInsensitive</c>, which affects READS but not WRITES, so without these the
/// body would go out PascalCase - accepted by the case-insensitive server binder but wrong on the
/// wire and invisible to any other consumer. Pinning the names makes the contract explicit instead
/// of dependent on a serializer setting elsewhere.
/// </remarks>
/// <param name="Path">Dotted path with optional <c>[index]</c> segments, e.g. <c>gateway.port</c>.</param>
/// <param name="Value">The value to write; ignored when <paramref name="Remove"/> is true.</param>
/// <param name="Remove">Remove the addressed node instead of setting it.</param>
public sealed record ConfigPatchOperationDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("value")] JsonNode? Value = null,
    [property: JsonPropertyName("remove")] bool Remove = false);

/// <summary>
/// An atomic batch of config changes with an optimistic-concurrency token (issue #2059).
/// </summary>
/// <param name="Operations">The changes to apply, in order. All or nothing.</param>
/// <param name="ExpectedRevision">Revision the client's snapshot was read at, or null to skip the check.</param>
public sealed record ConfigPatchRequestDto(
    [property: JsonPropertyName("operations")] IReadOnlyList<ConfigPatchOperationDto> Operations,
    [property: JsonPropertyName("expectedRevision")] string? ExpectedRevision = null);

/// <summary>
/// Server response to a config patch (issue #2059).
/// </summary>
/// <param name="Success">Whether the batch committed.</param>
/// <param name="Revision">The revision now on disk: the new one on success, the current one on conflict.</param>
/// <param name="Errors">Rejection messages; empty on success.</param>
public sealed record ConfigPatchResponseDto(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("revision")] string? Revision,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);
