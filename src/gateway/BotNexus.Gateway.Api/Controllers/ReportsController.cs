using System.IO.Abstractions;
using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Exposes read-only access to markdown reports under an agent workspace reports directory.
/// </summary>
[ApiController]
[Route("api/agents/{agentId}/reports")]
public sealed class ReportsController : ControllerBase
{
    private const string ReportsDirectoryName = "reports";

    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IFileSystem _fileSystem;
    private readonly int _maxFileReadBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportsController"/> class.
    /// </summary>
    /// <param name="agentRegistry">Agent registry used to validate known agent identifiers.</param>
    /// <param name="workspaceManager">Workspace manager used to resolve per-agent workspace roots.</param>
    /// <param name="fileSystem">Filesystem abstraction used for testable file-system reads.</param>
    /// <param name="platformConfig">Platform configuration (provides workspace portal limits).</param>
    public ReportsController(
        IAgentRegistry agentRegistry,
        IAgentWorkspaceManager workspaceManager,
        IFileSystem fileSystem,
        IOptions<PlatformConfig> platformConfig)
    {
        _agentRegistry = agentRegistry;
        _workspaceManager = workspaceManager;
        _fileSystem = fileSystem;
        _maxFileReadBytes = platformConfig.Value.Workspace?.MaxReportFileSizeBytes ?? 512 * 1024;
    }

    /// <summary>
    /// Lists markdown reports found under the workspace reports directory for an agent.
    /// </summary>
    /// <param name="agentId">Agent identifier whose reports directory should be listed.</param>
    /// <returns>Report metadata for readable markdown files scoped to the reports directory.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ReportsListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ReportsListResponse> GetReports(string agentId)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var reportsRoot = ResolveReportsRoot(validator);
        if (reportsRoot is null || !_fileSystem.Directory.Exists(reportsRoot.Value.ReportsPath))
        {
            return Ok(new ReportsListResponse { Reports = [] });
        }

        var reports = new List<ReportListItemDto>();
        foreach (var path in _fileSystem.Directory.EnumerateFileSystemEntries(reportsRoot.Value.ReportsPath))
        {
            if (_fileSystem.Directory.Exists(path))
                continue;

            var reportName = _fileSystem.Path.GetFileName(path);
            if (!IsSafeReportName(reportName))
                continue;

            var resolvedFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, path);
            if (!validator.CanRead(resolvedFinalPath))
                continue;

            if (!WorkspacePathSecurity.IsUnderPath(_fileSystem, resolvedFinalPath, reportsRoot.Value.ReportsFinalPath))
                continue;

            if (!_fileSystem.File.Exists(resolvedFinalPath))
                continue;

            var info = _fileSystem.FileInfo.New(resolvedFinalPath);
            reports.Add(new ReportListItemDto
            {
                Name = reportName,
                Size = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc
            });
        }

        return Ok(new ReportsListResponse
        {
            Reports = reports
                .OrderBy(report => report.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });
    }

    /// <summary>
    /// Reads markdown report content from the workspace reports directory for an agent.
    /// </summary>
    /// <param name="agentId">Agent identifier whose report should be read.</param>
    /// <param name="name">Report file name to load.</param>
    /// <returns>Report content and metadata when the report exists and is readable.</returns>
    [HttpGet("{**name}")]
    [ProducesResponseType(typeof(ReportContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ReportContentResponse> GetReport(string agentId, string name)
    {
        if (!_agentRegistry.Contains(AgentId.From(agentId)))
            return NotFound();

        if (!IsSafeReportName(name))
            return BadRequest(new { error = "name must be a markdown file name in the reports directory." });

        var workspaceRoot = WorkspacePathSecurity.NormalizePath(_fileSystem, _workspaceManager.GetWorkspacePath(agentId));
        var validator = new DefaultPathValidator(policy: null, workspaceRoot);
        var reportsRoot = ResolveReportsRoot(validator);
        if (reportsRoot is null)
            return StatusCode(StatusCodes.Status403Forbidden);

        var reportPath = _fileSystem.Path.Combine(ReportsDirectoryName, WorkspacePathSecurity.NormalizeRelativePath(_fileSystem, name));
        var resolvedReportPath = validator.ValidateAndResolve(reportPath, FileAccessMode.Read);
        if (resolvedReportPath is null)
            return StatusCode(StatusCodes.Status403Forbidden);

        var resolvedFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, resolvedReportPath);
        if (!validator.CanRead(resolvedFinalPath))
            return StatusCode(StatusCodes.Status403Forbidden);

        if (!WorkspacePathSecurity.IsUnderPath(_fileSystem, resolvedFinalPath, reportsRoot.Value.ReportsFinalPath))
            return StatusCode(StatusCodes.Status403Forbidden);

        if (_fileSystem.Directory.Exists(resolvedFinalPath))
            return BadRequest(new { error = "name must resolve to a report file." });

        if (!_fileSystem.File.Exists(resolvedFinalPath))
            return NotFound();

        var info = _fileSystem.FileInfo.New(resolvedFinalPath);
        var bytesToRead = (int)Math.Min(info.Length, _maxFileReadBytes);
        var buffer = new byte[bytesToRead];
        using var stream = _fileSystem.File.OpenRead(resolvedFinalPath);
        var bytesRead = stream.Read(buffer, 0, bytesToRead);
        if (IsBinary(buffer, bytesRead))
            return BadRequest(new { error = "report content must be UTF-8 text." });

        var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        return Ok(new ReportContentResponse
        {
            Name = _fileSystem.Path.GetFileName(resolvedFinalPath),
            // Size is the decoded character count of what was returned.
            // IsTruncated can be inferred by the caller when bytesRead < info.Length.
            Size = content.Length,
            IsTruncated = bytesRead < info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            Content = content,
            Encoding = "utf-8"
        });
    }

    private (string ReportsPath, string ReportsFinalPath)? ResolveReportsRoot(DefaultPathValidator validator)
    {
        var reportsPath = validator.ValidateAndResolve(ReportsDirectoryName, FileAccessMode.Read);
        if (reportsPath is null)
            return null;

        var reportsFinalPath = WorkspacePathSecurity.ResolveFinalTargetPath(_fileSystem, reportsPath);
        if (!validator.CanRead(reportsFinalPath))
            return null;

        return (reportsPath, reportsFinalPath);
    }

    private bool IsSafeReportName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Contains('\0'))
            return false;

        if (_fileSystem.Path.IsPathRooted(name))
            return false;

        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            return false;

        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return false;

        var invalidChars = _fileSystem.Path.GetInvalidFileNameChars();
        return name.IndexOfAny(invalidChars) < 0;
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
