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
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

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

        var workspaceRoot = NormalizePath(_workspaceManager.GetWorkspacePath(agentId));
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

        if (_fileSystem.Path.IsPathRooted(path))
            return BadRequest(new { error = "path must be workspace-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var workspaceRoot = NormalizePath(_workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var normalizedRelativePath = NormalizeRelativePath(path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelativePath, FileAccessMode.Read);
        if (resolvedPath is null)
            return Forbid();

        var resolvedFinalPath = ResolveFinalTargetPath(resolvedPath);
        if (!validator.CanRead(resolvedFinalPath))
            return Forbid();

        if (_fileSystem.Directory.Exists(resolvedFinalPath))
        {
            return Ok(new WorkspaceDirectoryResponse
            {
                Type = "directory",
                Path = ToWorkspaceRelativePath(workspaceRoot, resolvedFinalPath),
                DepthLimit = 0,
                Entries = BuildTreeEntries(workspaceRoot, resolvedFinalPath, validator, depthLimit: 0, currentDepth: 0)
            });
        }

        if (!_fileSystem.File.Exists(resolvedFinalPath))
            return NotFound();

        var relativePath = ToWorkspaceRelativePath(workspaceRoot, resolvedFinalPath);
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
            var finalPath = ResolveFinalTargetPath(entryPath);
            if (!validator.CanRead(finalPath))
                continue;

            var isDirectory = _fileSystem.Directory.Exists(entryPath);
            var relativePath = ToWorkspaceRelativePath(workspaceRoot, entryPath);
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

    private string ResolveFinalTargetPath(string fullPath)
    {
        var currentPath = NormalizePath(fullPath);
        var root = _fileSystem.Path.GetPathRoot(currentPath);
        if (string.IsNullOrWhiteSpace(root))
            return currentPath;

        var rootPath = NormalizePath(root);
        var segments = currentPath[rootPath.Length..]
            .Split(
                [_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var path = rootPath;
        for (var index = 0; index < segments.Length; index++)
        {
            path = _fileSystem.Path.Combine(path, segments[index]);
            if (_fileSystem.Directory.Exists(path))
            {
                var directoryInfo = _fileSystem.DirectoryInfo.New(path);
                if (directoryInfo.LinkTarget is null)
                    continue;

                var resolved = directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
                return NormalizePath(AppendRemainingSegments(resolved, segments, index + 1));
            }

            if (_fileSystem.File.Exists(path))
            {
                var fileInfo = _fileSystem.FileInfo.New(path);
                if (fileInfo.LinkTarget is null)
                    continue;

                var resolved = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
                return NormalizePath(AppendRemainingSegments(resolved, segments, index + 1));
            }

            break;
        }

        return currentPath;
    }

    private string AppendRemainingSegments(string basePath, IReadOnlyList<string> segments, int startIndex)
    {
        var path = basePath;
        for (var index = startIndex; index < segments.Count; index++)
            path = _fileSystem.Path.Combine(path, segments[index]);

        return path;
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

    private string NormalizePath(string path)
    {
        var fullPath = _fileSystem.Path.GetFullPath(path);
        var root = _fileSystem.Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, PathComparison))
            return fullPath;

        return fullPath.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar);
    }

    private string NormalizeRelativePath(string path)
    {
        return path.Trim()
            .Replace('/', _fileSystem.Path.DirectorySeparatorChar)
            .Replace('\\', _fileSystem.Path.DirectorySeparatorChar);
    }

    private string ToWorkspaceRelativePath(string workspaceRoot, string absolutePath)
    {
        var relativePath = _fileSystem.Path.GetRelativePath(workspaceRoot, absolutePath);
        return relativePath.Replace(_fileSystem.Path.DirectorySeparatorChar, '/');
    }
}
