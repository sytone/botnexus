using System.IO.Abstractions;
using System.Text;
using BotNexus.Extensions.Skills.Security;
using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Registers the Skills file-browser API endpoints (<c>/api/skills</c>) as minimal API routes.
/// Replaces the former SkillsController that lived in Gateway.Api.
/// </summary>
public sealed class SkillsEndpointContributor : IEndpointContributor
{
    private const int DefaultTreeDepthLimit = 2;
    private const int MaximumTreeDepthLimit = 5;
    private const int MaximumFileReadBytes = 512 * 1024;

    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/skills");

        group.MapGet("/", (IFileSystem fs, int depth = DefaultTreeDepthLimit) => GetSkillsRoot(fs, depth));
        // Usage telemetry read surface (#1833). Registered before the catch-all `/{**path}` route so
        // "telemetry" is matched as a literal segment rather than a skills-relative file path.
        group.MapGet("/telemetry", (ISkillUsageTelemetry? telemetry) => GetTelemetry(telemetry));
        group.MapGet("/telemetry/{skillName}", (string skillName, ISkillUsageTelemetry? telemetry) => GetTelemetryForSkill(skillName, telemetry));
        group.MapGet("/{**path}", (string path, IFileSystem fs) => GetSkillsPath(path, fs));
        group.MapPut("/{**path}", (string path, IFileSystem fs, HttpRequest req) => WriteSkillsPath(path, fs, req));
        group.MapDelete("/{**path}", (string path, IFileSystem fs, bool force = false) => DeleteSkillsPath(path, fs, force));
    }

    /// <summary>
    /// Returns all skill usage telemetry rows (#1833). Returns an empty collection when no telemetry
    /// sink is configured so the endpoint is always well-formed for admin/UI consumers.
    /// </summary>
    internal static async Task<IResult> GetTelemetry(ISkillUsageTelemetry? telemetry)
    {
        if (telemetry is null)
            return Results.Ok(new SkillUsageTelemetryResponse { Skills = [] });

        var records = await telemetry.GetAllAsync();
        return Results.Ok(new SkillUsageTelemetryResponse
        {
            Skills = records.Select(ToDto).ToList()
        });
    }

    /// <summary>
    /// Returns the telemetry row for a single skill (#1833), or 404 when the skill has no recorded
    /// activity or no telemetry sink is configured.
    /// </summary>
    internal static async Task<IResult> GetTelemetryForSkill(string skillName, ISkillUsageTelemetry? telemetry)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return Results.BadRequest(new { error = "skillName is required." });

        if (telemetry is null)
            return Results.NotFound();

        var record = await telemetry.GetAsync(skillName);
        return record is null ? Results.NotFound() : Results.Ok(ToDto(record));
    }

    private static SkillUsageDto ToDto(SkillUsageRecord record) => new()
    {
        SkillName = record.SkillName,
        ViewCount = record.ViewCount,
        UseCount = record.UseCount,
        PatchCount = record.PatchCount,
        LastUsedAt = record.LastUsedAt,
        CreatedBy = record.CreatedBy,
        Pinned = record.Pinned
    };

    internal static IResult GetSkillsRoot(IFileSystem fileSystem, int depth = DefaultTreeDepthLimit)
        => GetSkillsRoot(fileSystem, GetSkillsRootPath(), depth);

    internal static IResult GetSkillsRoot(IFileSystem fileSystem, string skillsRoot, int depth = DefaultTreeDepthLimit)
    {
        if (depth is < 0 or > MaximumTreeDepthLimit)
            return Results.BadRequest(new { error = $"depth must be between 0 and {MaximumTreeDepthLimit}." });
        var normalizedRoot = NormalizePath(fileSystem, skillsRoot);

        if (!fileSystem.Directory.Exists(normalizedRoot))
        {
            return Results.Ok(new SkillsDirectoryResponse
            {
                Path = string.Empty,
                DepthLimit = depth,
                Entries = []
            });
        }

        var entries = BuildTreeEntries(fileSystem, normalizedRoot, normalizedRoot, depth, 0);
        return Results.Ok(new SkillsDirectoryResponse
        {
            Path = string.Empty,
            DepthLimit = depth,
            Entries = entries
        });
    }

    internal static IResult GetSkillsPath(string path, IFileSystem fileSystem)
        => GetSkillsPath(path, fileSystem, GetSkillsRootPath());

    internal static IResult GetSkillsPath(string path, IFileSystem fileSystem, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return Results.BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return Results.BadRequest(new { error = "path contains invalid characters." });
        var normalizedRoot = NormalizePath(fileSystem, skillsRoot);
        var combinedPath = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(normalizedRoot, NormalizeRelativePath(fileSystem, path)));

        if (!ValidateContainment(fileSystem, combinedPath, normalizedRoot))
            return Results.Forbid();

        if (!SkillPathValidator.TryValidate(combinedPath, normalizedRoot, fileSystem, out var resolvedPath, out _))
            return Results.Forbid();

        if (fileSystem.Directory.Exists(resolvedPath))
        {
            return Results.Ok(new SkillsDirectoryResponse
            {
                Type = "directory",
                Path = ToRelativePath(fileSystem, normalizedRoot, resolvedPath),
                DepthLimit = 0,
                Entries = BuildTreeEntries(fileSystem, normalizedRoot, resolvedPath, depthLimit: 0, currentDepth: 0)
            });
        }

        if (!fileSystem.File.Exists(resolvedPath))
            return Results.NotFound();

        var relativePath = ToRelativePath(fileSystem, normalizedRoot, resolvedPath);
        var fileInfo = fileSystem.FileInfo.New(resolvedPath);
        var fileSize = fileInfo.Length;
        var bytesToRead = (int)Math.Min(fileSize, MaximumFileReadBytes);
        var buffer = new byte[bytesToRead];
        using var stream = fileSystem.File.OpenRead(resolvedPath);
        var bytesRead = stream.Read(buffer, 0, bytesToRead);
        var truncated = fileSize > MaximumFileReadBytes;

        if (IsBinary(buffer, bytesRead))
        {
            return Results.Ok(new SkillsFileResponse
            {
                Path = relativePath,
                Type = "binary",
                Size = fileSize,
                IsTruncated = truncated
            });
        }

        return Results.Ok(new SkillsFileResponse
        {
            Path = relativePath,
            Type = "text",
            Size = fileSize,
            Encoding = "utf-8",
            Content = Encoding.UTF8.GetString(buffer, 0, bytesRead),
            IsTruncated = truncated
        });
    }

    internal static async Task<IResult> WriteSkillsPath(string path, IFileSystem fileSystem, HttpRequest request)
        => await WriteSkillsPath(path, fileSystem, request, GetSkillsRootPath());

    internal static async Task<IResult> WriteSkillsPath(string path, IFileSystem fileSystem, HttpRequest request, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return Results.BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return Results.BadRequest(new { error = "path contains invalid characters." });

        var normalizedRoot = NormalizePath(fileSystem, skillsRoot);
        var combinedPath = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(normalizedRoot, NormalizeRelativePath(fileSystem, path)));

        if (!ValidateContainment(fileSystem, combinedPath, normalizedRoot))
            return Results.Forbid();

        if (!SkillPathValidator.TryValidate(combinedPath, normalizedRoot, fileSystem, out var resolvedPath, out _))
            return Results.Forbid();

        if (fileSystem.Directory.Exists(resolvedPath))
            return Results.BadRequest(new { error = "Path refers to a directory, not a file." });

        var parentDir = fileSystem.Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(parentDir) && !fileSystem.Directory.Exists(parentDir))
            return Results.BadRequest(new { error = "Parent directory does not exist." });

        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<SkillsWriteRequest>(
            request.Body, cancellationToken: request.HttpContext.RequestAborted);

        if (body is null)
            return Results.BadRequest(new { error = "Request body is required." });

        fileSystem.File.WriteAllText(resolvedPath, body.Content, Encoding.UTF8);
        return Results.NoContent();
    }

    internal static IResult DeleteSkillsPath(string path, IFileSystem fileSystem, bool force = false)
        => DeleteSkillsPath(path, fileSystem, GetSkillsRootPath(), force);

    internal static IResult DeleteSkillsPath(string path, IFileSystem fileSystem, string skillsRoot, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "path is required." });

        if (IsAbsolutePath(path))
            return Results.BadRequest(new { error = "path must be skills-relative." });

        if (path.Contains('\0'))
            return Results.BadRequest(new { error = "path contains invalid characters." });
        var normalizedRoot = NormalizePath(fileSystem, skillsRoot);
        var combinedPath = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(normalizedRoot, NormalizeRelativePath(fileSystem, path)));

        if (!ValidateContainment(fileSystem, combinedPath, normalizedRoot))
            return Results.Forbid();

        if (!SkillPathValidator.TryValidate(combinedPath, normalizedRoot, fileSystem, out var resolvedPath, out _))
            return Results.Forbid();

        if (fileSystem.Directory.Exists(resolvedPath))
        {
            var isEmpty = !fileSystem.Directory.EnumerateFileSystemEntries(resolvedPath).Any();
            if (!isEmpty && !force)
                return Results.Conflict(new { error = "Directory is not empty. Use force=true to delete recursively." });
            fileSystem.Directory.Delete(resolvedPath, recursive: true);
            return Results.NoContent();
        }

        if (!fileSystem.File.Exists(resolvedPath))
            return Results.NotFound();

        fileSystem.File.Delete(resolvedPath);
        return Results.NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static string GetSkillsRootPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".botnexus", "skills");
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path)
        || (path.Length >= 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'))
        || path.StartsWith('/')
        || path.StartsWith('\\');

    private static string NormalizePath(IFileSystem fileSystem, string path)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        return fullPath.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(IFileSystem fileSystem, string path)
    {
        return path.Trim()
            .Replace('/', fileSystem.Path.DirectorySeparatorChar)
            .Replace('\\', fileSystem.Path.DirectorySeparatorChar);
    }

    private static bool ValidateContainment(IFileSystem fileSystem, string candidatePath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedCandidate = NormalizePath(fileSystem, candidatePath);
        var normalizedRoot = NormalizePath(fileSystem, rootPath);

        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
            return true;

        var sep = fileSystem.Path.DirectorySeparatorChar.ToString();
        return normalizedCandidate.StartsWith(normalizedRoot + sep, comparison);
    }

    private static string ToRelativePath(IFileSystem fileSystem, string root, string absolutePath)
    {
        var relativePath = fileSystem.Path.GetRelativePath(root, absolutePath);
        return relativePath.Replace(fileSystem.Path.DirectorySeparatorChar, '/');
    }

    private static IReadOnlyList<SkillsEntryDto> BuildTreeEntries(
        IFileSystem fileSystem,
        string root,
        string currentDirectory,
        int depthLimit,
        int currentDepth)
    {
        var entries = new List<SkillsEntryDto>();
        var items = fileSystem.Directory
            .EnumerateFileSystemEntries(currentDirectory)
            .OrderBy(p => fileSystem.Directory.Exists(p) ? 0 : 1)
            .ThenBy(p => fileSystem.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        foreach (var itemPath in items)
        {
            if (!SkillPathValidator.TryValidate(itemPath, root, fileSystem, out var resolvedItem, out _))
                continue;

            var isDir = fileSystem.Directory.Exists(itemPath);
            var relativePath = ToRelativePath(fileSystem, root, itemPath);
            if (isDir)
            {
                var children = currentDepth < depthLimit
                    ? BuildTreeEntries(fileSystem, root, itemPath, depthLimit, currentDepth + 1)
                    : [];
                entries.Add(new SkillsEntryDto
                {
                    Name = fileSystem.Path.GetFileName(itemPath),
                    Path = relativePath,
                    Type = "directory",
                    Children = children
                });
                continue;
            }

            var info = fileSystem.FileInfo.New(itemPath);
            entries.Add(new SkillsEntryDto
            {
                Name = fileSystem.Path.GetFileName(itemPath),
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
