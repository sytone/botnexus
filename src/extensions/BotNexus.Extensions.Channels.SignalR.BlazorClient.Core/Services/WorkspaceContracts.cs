using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

#pragma warning disable CS1591 // REST DTOs — public API via JSON deserialization

public sealed record WorkspaceEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("size")] long? Size);

public sealed record ReportListItemDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("lastModifiedUtc")] DateTimeOffset? LastModifiedUtc);

public sealed record ReportsListResponseDto(
    [property: JsonPropertyName("reports")] IReadOnlyList<ReportListItemDto>? Reports);

public sealed record ReportContentDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("isTruncated")] bool? IsTruncated,
    [property: JsonPropertyName("lastModifiedUtc")] DateTimeOffset? LastModifiedUtc,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("encoding")] string? Encoding);

public sealed record WorkspaceResponseDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("entries")] IReadOnlyList<WorkspaceEntryDto>? Entries,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("encoding")] string? Encoding,
    [property: JsonPropertyName("isTruncated")] bool? IsTruncated,
    [property: JsonPropertyName("binary")] bool? Binary);

/// <summary>Loaded extension detail response from GET /api/extensions/details.</summary>
public sealed record ExtensionDetailDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("extensionTypes")] IReadOnlyList<string>? ExtensionTypes,
    [property: JsonPropertyName("registeredServices")] IReadOnlyList<string>? RegisteredServices,
    [property: JsonPropertyName("assemblyFileName")] string? AssemblyFileName);
