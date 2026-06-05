using System.IO.Abstractions;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Exposes read-only access to files under an agent workspace directory.
/// </summary>
[ApiController]
[Route("api/agents/{agentId}/workspace")]
public sealed class WorkspaceController : ControllerBase
{
    private const int DefaultTreeDepthLimit = 2;
    private const int MaximumTreeDepthLimit = 5;
    private const int MaximumFileReadBytes = 512 * 1024;

    /// <summary>
    /// Returns true if <paramref name="path"/> is an absolute/rooted path on any platform.
    /// <see cref="System.IO.Path"/> only recognises the host OS convention;
    /// this helper additionally catches Windows drive-letter paths when running on Linux.
    /// </summary>
    private static bool IsAbsolutePath(string path) =>
        System.IO.Path.IsPathRooted(path)                                                   // Linux: /foo  Windows: \foo or C:\foo
        || (path.Length >= 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'))  // C:/ or C:\ on Linux runner
        || path.StartsWith('/')                                                             // belt-and-braces
        || path.StartsWith('\\');                                                          // UNC \\server\share
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceController"/> class.
    /// </summary>
    /// <param name="agentRegistry">Agent registry used to validate known agent identifiers.</param>
    /// <param name="workspaceManager">Workspace manager used to resolve per-agent workspace roots.</param>
    /// <param name="fileSystem">Filesystem abstraction used for testable file-system reads.</param>
    public WorkspaceController(
        IAgentRegistry agentRegistry,
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem)
    {
        _agentRegistry = agentRegistry;
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Returns a depth-limited directory tree rooted at an agent workspace path.
    /// </summary>
    /// <param name="agentId">Agent identifier whose workspace should be listed.</param>
    /// <param name="depth">Maximum depth to include in the response tree.</param>
    /// <returns>Workspace tree entries rooted at the workspace directory.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(WorkspaceDirectoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WorkspaceDirectoryResponse> GetWorkspace(string agentId, [FromQuery] int depth = DefaultTreeDepthLimit)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        if (depth is < 0 or > MaximumTreeDepthLimit)
        {
            return BadRequest(new { error = $"depth must be between 0 and {MaximumTreeDepthLimit}." });
        }

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        if (!_fileSystem.Directory.Exists(workspaceRoot))
        {
            return Ok(new WorkspaceDirectoryResponse
            {
                Path = string.Empty,
                DepthLimit = depth,
                Entries = []
            });
        }

        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var entries = BuildTreeEntries(workspaceRoot, workspaceRoot, validator, depth, 0);
        return Ok(new WorkspaceDirectoryResponse
        {
            Path = string.Empty,
            DepthLimit = depth,
            Entries = entries
        });
    }

    /// <summary>
    /// Returns file content for a workspace-relative path inside an agent workspace.
    /// </summary>
    /// <param name="agentId">Agent identifier whose workspace should be read.</param>
    /// <param name="path">Workspace-relative file path.</param>
    /// <returns>File metadata and textual content when the target is a readable text file.</returns>
    [HttpGet("{**path}")]
    [ProducesResponseType(typeof(WorkspaceDirectoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkspaceFileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetFile(string agentId, string path)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be workspace-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var normalizedRelativePath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelativePath, FileAccessMode.Read);
        if (resolvedPath is null)
            return Forbid();

        var resolvedFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, resolvedPath);
        if (!validator.CanRead(resolvedFinalPath))
            return Forbid();

        if (_fileSystem.Directory.Exists(resolvedFinalPath))
        {
            return Ok(new WorkspaceDirectoryResponse
            {
                Type = "directory",
                Path = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, workspaceRoot, resolvedFinalPath),
                DepthLimit = 0,
                Entries = BuildTreeEntries(workspaceRoot, resolvedFinalPath, validator, depthLimit: 0, currentDepth: 0)
            });
        }

        if (!_fileSystem.File.Exists(resolvedFinalPath))
            return NotFound();

        var relativePath = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, workspaceRoot, resolvedFinalPath);
        var fileInfo = _fileSystem.FileInfo.New(resolvedFinalPath);
        var fileSize = fileInfo.Length;
        var bytesToRead = (int)Math.Min(fileSize, MaximumFileReadBytes);
        var buffer = new byte[bytesToRead];
        using var stream = _fileSystem.File.OpenRead(resolvedFinalPath);
        var bytesRead = stream.Read(buffer, 0, bytesToRead);

        var truncated = fileSize > MaximumFileReadBytes;
        if (IsBinary(buffer, bytesRead))
        {
            return Ok(new WorkspaceFileResponse
            {
                Path = relativePath,
                Type = "binary",
                Size = fileSize,
                IsTruncated = truncated
            });
        }

        return Ok(new WorkspaceFileResponse
        {
            Path = relativePath,
            Type = "text",
            Size = fileSize,
            Encoding = "utf-8",
            Content = Encoding.UTF8.GetString(buffer, 0, bytesRead),
            IsTruncated = truncated
        });
    }

    private IReadOnlyList<WorkspaceEntryDto> BuildTreeEntries(
        string workspaceRoot,
        string currentDirectory,
        DefaultPathValidator validator,
        int depthLimit,
        int currentDepth)
    {
        var entries = new List<WorkspaceEntryDto>();
        var fileSystemEntries = _fileSystem.Directory
            .EnumerateFileSystemEntries(currentDirectory)
            .OrderBy(path => _fileSystem.Directory.Exists(path) ? 0 : 1)
            .ThenBy(path => _fileSystem.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

        foreach (var entryPath in fileSystemEntries)
        {
            var finalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, entryPath);
            if (!validator.CanRead(finalPath))
                continue;

            var isDirectory = _fileSystem.Directory.Exists(entryPath);
            var relativePath = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, workspaceRoot, entryPath);
            if (isDirectory)
            {
                var children = currentDepth < depthLimit
                    ? BuildTreeEntries(workspaceRoot, entryPath, validator, depthLimit, currentDepth + 1)
                    : [];
                entries.Add(new WorkspaceEntryDto
                {
                    Name = _fileSystem.Path.GetFileName(entryPath),
                    Path = relativePath,
                    Type = "directory",
                    Children = children
                });
                continue;
            }

            var info = _fileSystem.FileInfo.New(entryPath);
            entries.Add(new WorkspaceEntryDto
            {
                Name = _fileSystem.Path.GetFileName(entryPath),
                Path = relativePath,
                Type = "file",
                Size = info.Length
            });
        }

        return entries;
    }

    /// <summary>
    /// Deletes a file or directory at a workspace-relative path.
    /// </summary>
    /// <param name="agentId">Agent identifier whose workspace contains the target.</param>
    /// <param name="path">Workspace-relative path to the file or directory to delete.</param>
    /// <param name="force">When <c>true</c>, allows deletion of non-empty directories.</param>
    /// <returns>204 on success; 404 if not found; 403 if path escapes workspace; 409 if directory is non-empty and force is false.</returns>
    [HttpDelete("{**path}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DeleteItem(string agentId, string path, [FromQuery] bool force = false)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be workspace-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var normalizedRelativePath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelativePath, FileAccessMode.Write);
        if (resolvedPath is null)
            return Forbid();

        var resolvedFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, resolvedPath);
        if (!validator.CanWrite(resolvedFinalPath))
            return Forbid();

        if (_fileSystem.Directory.Exists(resolvedFinalPath))
        {
            var isEmpty = !_fileSystem.Directory.EnumerateFileSystemEntries(resolvedFinalPath).Any();
            if (!isEmpty && !force)
                return Conflict(new { error = "Directory is not empty. Use force=true to delete recursively." });

            _fileSystem.Directory.Delete(resolvedFinalPath, recursive: true);
            return NoContent();
        }

        if (!_fileSystem.File.Exists(resolvedFinalPath))
            return NotFound();

        _fileSystem.File.Delete(resolvedFinalPath);
        return NoContent();
    }

    /// <summary>
    /// Writes text content to a workspace-relative file path.
    /// </summary>
    /// <param name="agentId">Agent identifier whose workspace should be written to.</param>
    /// <param name="path">Workspace-relative file path.</param>
    /// <param name="request">Request body containing the text content to write.</param>
    /// <returns>204 on success; 400/403 as appropriate.</returns>
    [HttpPut("{**path}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult WriteFile(string agentId, string path, [FromBody] WorkspaceWriteRequest request)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be workspace-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var normalizedRelativePath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelativePath, FileAccessMode.Write);
        if (resolvedPath is null)
            return Forbid();

        var resolvedFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, resolvedPath);
        if (!validator.CanWrite(resolvedFinalPath))
            return Forbid();

        if (_fileSystem.Directory.Exists(resolvedFinalPath))
            return BadRequest(new { error = "Path refers to a directory, not a file." });

        var parentDir = _fileSystem.Path.GetDirectoryName(resolvedFinalPath);
        if (!string.IsNullOrEmpty(parentDir) && !_fileSystem.Directory.Exists(parentDir))
            return BadRequest(new { error = "Parent directory does not exist." });

        // Use BOM-free UTF-8: Encoding.UTF8 emits a BOM on Windows which breaks YAML
        // frontmatter parsers (e.g. SkillParser) and agent workspace file consumers.
        var noBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _fileSystem.File.WriteAllText(resolvedFinalPath, request.Content, noBomUtf8);
        return NoContent();
    }

    private static bool IsBinary(byte[] buffer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            if (buffer[index] == 0)
                return true;
        }

        return false;
    }

}
