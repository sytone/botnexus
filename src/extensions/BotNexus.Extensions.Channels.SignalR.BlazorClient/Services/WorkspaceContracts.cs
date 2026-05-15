using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

#pragma warning disable CS1591 // REST DTOs — public API via JSON deserialization

public sealed record WorkspaceEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long? Size);

public sealed record WorkspaceResponseDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("entries")] IReadOnlyList<WorkspaceEntryDto>? Entries,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("encoding")] string? Encoding,
    [property: JsonPropertyName("isTruncated")] bool? IsTruncated,
    [property: JsonPropertyName("binary")] bool? Binary);
