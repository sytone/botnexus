namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// Describes a single file-system entry within an agent workspace tree.
/// </summary>
public sealed class WorkspaceEntryDto
{
    /// <summary>
    /// Gets or sets the entry name relative to its parent directory.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the workspace-relative path for this entry.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entry type (<c>file</c> or <c>directory</c>).
    /// </summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Gets or sets the file size in bytes when <see cref="Type"/> is <c>file</c>.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Gets or sets child entries for directory nodes.
    /// </summary>
    public IReadOnlyList<WorkspaceEntryDto> Children { get; set; } = [];
}

/// <summary>
/// Response body for workspace directory tree reads.
/// </summary>
public sealed class WorkspaceDirectoryResponse
{
    /// <summary>
    /// Gets or sets the workspace-relative path represented by the response root.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum traversal depth applied while building the tree.
    /// </summary>
    public int DepthLimit { get; set; }

    /// <summary>
    /// Gets or sets the entries discovered under the response root.
    /// </summary>
    public IReadOnlyList<WorkspaceEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// Response body for workspace file reads.
/// </summary>
public sealed class WorkspaceFileResponse
{
    /// <summary>
    /// Gets or sets the workspace-relative file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file type (<c>text</c> or <c>binary</c>).
    /// </summary>
    public string Type { get; set; } = "text";

    /// <summary>
    /// Gets or sets the decoded text content when <see cref="Type"/> is <c>text</c>.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the content encoding when textual content is returned.
    /// </summary>
    public string? Encoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether content was truncated to the API read cap.
    /// </summary>
    public bool Truncated { get; set; }
}
