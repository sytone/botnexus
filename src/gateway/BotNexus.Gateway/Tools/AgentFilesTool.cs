using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// On-demand tool that returns the <c>AGENTS.md</c> convention files that apply to a
/// given path, by walking from that path upward through its parent directories to the
/// nearest git repository root (the directory containing a <c>.git</c> entry).
/// </summary>
/// <remarks>
/// <para>
/// This is the pull-based counterpart to the old always-on prompt injection. Rather than
/// slurping every discoverable <c>AGENTS.md</c> into the system prompt on every turn (which
/// could embed hundreds of files and exhaust the context window), the agent calls this tool
/// with the specific path it is working in and receives only the relevant chain of files.
/// </para>
/// <para>
/// Access is gated by the agent's <see cref="FileAccessPolicy"/> via <see cref="IPathValidator"/>:
/// the requested path must be readable by the agent, otherwise the call is refused. The grant
/// is the trust boundary, so an arbitrary untrusted repo is never read.
/// </para>
/// <para>
/// Files are returned root-first (most general to most specific). Each file is defensively
/// capped in size so a single pathological <c>AGENTS.md</c> cannot itself blow the budget.
/// </para>
/// </remarks>
public sealed class AgentFilesTool : IAgentTool
{
    private const string AgentsMdFileName = "AGENTS.md";
    private const string GitDirectoryName = ".git";

    /// <summary>Maximum bytes returned per file before truncation.</summary>
    private const int MaxBytesPerFile = 16 * 1024;

    /// <summary>Maximum directory levels to walk upward before giving up (safety bound).</summary>
    private const int MaxWalkDepth = 64;

    private readonly IFileSystem _fileSystem;
    private readonly IPathValidator? _pathValidator;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFilesTool"/> class.
    /// </summary>
    /// <param name="pathValidator">
    /// Validator enforcing the agent's file-access policy. When <see langword="null"/>, no
    /// access gating is applied (used only in tests / unrestricted contexts).
    /// </param>
    /// <param name="fileSystem">Filesystem abstraction; defaults to the real filesystem.</param>
    public AgentFilesTool(IPathValidator? pathValidator = null, IFileSystem? fileSystem = null)
    {
        _pathValidator = pathValidator;
        _fileSystem = fileSystem ?? new FileSystem();
    }

    /// <inheritdoc />
    public string Name => "get_agent_files";

    /// <inheritdoc />
    public string Label => "Get Agent Files";

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Load the AGENTS.md convention files that apply to a directory you are working in. "
        + "Walks from the given path up through its parent directories to the repository root "
        + "(.git boundary) and returns any AGENTS.md files found, most-general first. "
        + "Call this when you start working in a repo or directory to pick up its conventions "
        + "before creating or editing files there.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string",
                  "description": "A directory or file inside the tree you are working in. The tool walks this path and its parents up to the nearest .git root."
                }
              },
              "required": ["path"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ReadString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Missing required argument: path.");
        return Task.FromResult(arguments);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawPath = ReadString(arguments, "path")!;

        // Enforce the agent's read policy. The grant is the trust boundary.
        var resolved = _pathValidator?.ValidateAndResolve(rawPath, FileAccessMode.Read);
        if (_pathValidator is not null && resolved is null)
            return Task.FromResult(TextResult($"Access denied: path '{rawPath}' is not permitted for read."));

        string fullPath;
        try
        {
            fullPath = resolved ?? _fileSystem.Path.GetFullPath(rawPath);
        }
        catch
        {
            return Task.FromResult(TextResult($"Error: cannot resolve path '{rawPath}'."));
        }

        // Reduce a file path to its containing directory so the walk starts from a directory.
        var startDir = _fileSystem.Directory.Exists(fullPath)
            ? fullPath
            : _fileSystem.Path.GetDirectoryName(fullPath);

        if (string.IsNullOrEmpty(startDir))
            return Task.FromResult(TextResult($"Error: cannot determine a directory for path '{rawPath}'."));

        var files = CollectAgentsMdFiles(startDir);
        if (files.Count == 0)
        {
            return Task.FromResult(TextResult(
                $"No AGENTS.md files found from '{startDir}' up to the repository root."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {files.Count} AGENTS.md file(s) for '{startDir}' (most general first):");
        sb.AppendLine();
        foreach (var (filePath, content) in files)
        {
            sb.AppendLine($"<!-- AGENTS.md: {filePath} -->");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return Task.FromResult(TextResult(sb.ToString().TrimEnd()));
    }

    /// <summary>
    /// Walks from <paramref name="startPath"/> upward through parent directories to the nearest
    /// git repository root (a directory containing a <c>.git</c> entry), collecting
    /// <c>AGENTS.md</c> files ordered root-first. Returns an empty list when the path is not
    /// inside a git repository or no files are found.
    /// </summary>
    internal List<(string Path, string Content)> CollectAgentsMdFiles(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return [];

        var chain = new List<string>();
        string? repoRoot = null;
        var current = startPath;
        var depth = 0;

        while (!string.IsNullOrEmpty(current) && depth++ < MaxWalkDepth)
        {
            var gitDir = _fileSystem.Path.Combine(current, GitDirectoryName);
            if (_fileSystem.Directory.Exists(gitDir) || _fileSystem.File.Exists(gitDir))
            {
                repoRoot = current;
                break;
            }

            chain.Add(current);
            var parent = _fileSystem.Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }

        if (repoRoot is null)
            return []; // Not inside a git repository.

        chain.Add(repoRoot);
        chain.Reverse(); // Root-first ordering.

        var result = new List<(string, string)>();
        foreach (var dir in chain)
        {
            var agentsMdPath = _fileSystem.Path.Combine(dir, AgentsMdFileName);
            if (!_fileSystem.File.Exists(agentsMdPath))
                continue;

            try
            {
                var content = _fileSystem.File.ReadAllText(agentsMdPath);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                content = content.Trim();
                if (Encoding.UTF8.GetByteCount(content) > MaxBytesPerFile)
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    content = Encoding.UTF8.GetString(bytes, 0, MaxBytesPerFile)
                        + "\n\n... [truncated: file exceeds " + MaxBytesPerFile + " bytes]";
                }

                result.Add((agentsMdPath, content));
            }
            catch
            {
                // Skip unreadable files - never crash the tool call.
            }
        }

        return result;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
