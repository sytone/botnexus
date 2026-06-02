using System.IO.Abstractions;
using System.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Exposes filesystem access to the global BotNexus skills directory
/// (<c>~/.botnexus/skills</c>).  Used by the portal Skills Explorer.
/// </summary>
[ApiController]
[Route("api/skills")]
public sealed class SkillsController : ControllerBase
{
    private const int DefaultTreeDepthLimit = 2;
    private const int MaximumTreeDepthLimit = 5;
    private const int MaximumFileReadBytes = 512 * 1024;

    private static bool IsAbsolutePath(string path) =>
        System.IO.Path.IsPathRooted(path)
        || (path.Length >= 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'))
        || path.StartsWith('/')
        || path.StartsWith('\\');

    private readonly IFileSystem _fileSystem;
    private readonly string _skillsRoot;

    /// <summary>
    /// Initializes a new instance of <see cref="SkillsController"/>.
    /// </summary>
    public SkillsController(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _skillsRoot = System.IO.Path.Combine(home, ".botnexus", "skills");
    }

    /// <summary>
    /// Returns a depth-limited directory listing of the global skills root.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(WorkspaceDirectoryResponse), StatusCodes.Status200OK)]
    public ActionResult<WorkspaceDirectoryResponse> GetSkills([FromQuery] int depth = DefaultTreeDepthLimit)
    {
        if (depth is < 0 or > MaximumTreeDepthLimit)
            return BadRequest(new { error = $"depth must be between 0 and {MaximumTreeDepthLimit}." });

        var normalizedRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _skillsRoot);

        if (!_fileSystem.Directory.Exists(normalizedRoot))
        {
            return Ok(new WorkspaceDirectoryResponse
            {
                Path = string.Empty,
                DepthLimit = depth,
                Entries = []
            });
        }

        var validator = new DefaultPathValidator(policy: null, normalizedRoot);
        var entries = BuildTreeEntries(normalizedRoot, normalizedRoot, validator, depth, 0);
        return Ok(new WorkspaceDirectoryResponse
        {
            Path = string.Empty,
            DepthLimit = depth,
            Entries = entries
        });
    }

    /// <summary>
    /// Returns file content or a directory listing for a skills-relative path.
    /// </summary>
    [HttpGet("{**path}")]
    [ProducesResponseType(typeof(WorkspaceDirectoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkspaceFileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var normalizedRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _skillsRoot);
        var validator = new DefaultPathValidator(policy: null, normalizedRoot);
        var normalizedRelPath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelPath, FileAccessMode.Read);
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
                Path = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, normalizedRoot, resolvedFinalPath),
                DepthLimit = 0,
                Entries = BuildTreeEntries(normalizedRoot, resolvedFinalPath, validator, depthLimit: 0, currentDepth: 0)
            });
        }

        if (!_fileSystem.File.Exists(resolvedFinalPath))
            return NotFound();

        var relativePath = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, normalizedRoot, resolvedFinalPath);
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

    /// <summary>
    /// Writes text content to a skills-relative file path.
    /// </summary>
    [HttpPut("{**path}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult WriteFile(string path, [FromBody] WorkspaceWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var normalizedRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _skillsRoot);
        var validator = new DefaultPathValidator(policy: null, normalizedRoot);
        var normalizedRelPath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelPath, FileAccessMode.Write);
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

        _fileSystem.File.WriteAllText(resolvedFinalPath, request.Content, Encoding.UTF8);
        return NoContent();
    }

    /// <summary>
    /// Deletes a file or directory at a skills-relative path.
    /// </summary>
    [HttpDelete("{**path}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DeleteItem(string path, [FromQuery] bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return BadRequest(new { error = "path contains invalid characters." });

        var normalizedRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _skillsRoot);
        var validator = new DefaultPathValidator(policy: null, normalizedRoot);
        var normalizedRelPath = WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, path);
        var resolvedPath = validator.ValidateAndResolve(normalizedRelPath, FileAccessMode.Write);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<WorkspaceEntryDto> BuildTreeEntries(
        string root,
        string currentDirectory,
        DefaultPathValidator validator,
        int depthLimit,
        int currentDepth)
    {
        var entries = new List<WorkspaceEntryDto>();
        var items = _fileSystem.Directory
            .EnumerateFileSystemEntries(currentDirectory)
            .OrderBy(p => _fileSystem.Directory.Exists(p) ? 0 : 1)
            .ThenBy(p => _fileSystem.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        foreach (var itemPath in items)
        {
            var finalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, itemPath);
            if (!validator.CanRead(finalPath))
                continue;

            var isDir = _fileSystem.Directory.Exists(itemPath);
            var relativePath = WorkspacePathSecurity.ToWorkspaceRelativePath(_fileSystem, root, itemPath);
            if (isDir)
            {
                var children = currentDepth < depthLimit
                    ? BuildTreeEntries(root, itemPath, validator, depthLimit, currentDepth + 1)
                    : [];
                entries.Add(new WorkspaceEntryDto
                {
                    Name = _fileSystem.Path.GetFileName(itemPath),
                    Path = relativePath,
                    Type = "directory",
                    Children = children
                });
                continue;
            }

            var info = _fileSystem.FileInfo.New(itemPath);
            entries.Add(new WorkspaceEntryDto
            {
                Name = _fileSystem.Path.GetFileName(itemPath),
                Path = relativePath,
                Type = "file",
                Size = info.Length
            });
        }

        return entries;
    }

    private static bool IsBinary(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] == 0)
                return true;
        }
        return false;
    }
}
