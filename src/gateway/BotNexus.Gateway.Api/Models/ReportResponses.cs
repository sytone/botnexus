namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// Describes a single report file discovered in an agent workspace reports directory.
/// </summary>
public sealed class ReportListItemDto
{
    /// <summary>
    /// Gets or sets the report file name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the report file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last write time in UTC.
    /// </summary>
    public DateTimeOffset LastModifiedUtc { get; set; }
}

/// <summary>
/// Response body for listing report files in an agent workspace.
/// </summary>
public sealed class ReportsListResponse
{
    /// <summary>
    /// Gets or sets the reports discovered under the workspace reports directory.
    /// </summary>
    public IReadOnlyList<ReportListItemDto> Reports { get; set; } = [];
}

/// <summary>
/// Response body for reading report file content.
/// </summary>
public sealed class ReportContentResponse
{
    /// <summary>
    /// Gets or sets the report file name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the report file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the last write time in UTC.
    /// </summary>
    public DateTimeOffset LastModifiedUtc { get; set; }

    /// <summary>
    /// Gets or sets the report text content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content encoding when textual content is returned.
    /// </summary>
    public string Encoding { get; set; } = "utf-8";
}
