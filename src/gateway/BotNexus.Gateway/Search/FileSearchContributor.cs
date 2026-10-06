using System.IO.Abstractions;
using System.Text;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Searches readable files in registered agent workspaces while applying each agent's existing
/// file-access policy to the workspace root and every candidate file.
/// </summary>
public sealed class FileSearchContributor(
    IAgentRegistry agentRegistry,
    IAgentWorkspaceManager workspaceManager,
    IFileSystem fileSystem) : ISearchContributor
{
    internal const int MaxSnippetLength = 240;
    private const int MaxFileBytes = 1024 * 1024;

    /// <inheritdoc />
    public string SourceId => "files";

    /// <inheritdoc />
    public string Label => "Files";

    /// <inheritdoc />
    public bool IsAvailable => SearchableAgents().Any(agent => fileSystem.Directory.Exists(agent.WorkspacePath));

    /// <inheritdoc />
    public bool CanAssessProvenanceTrust => false;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Query) || request.MaxResults <= 0)
        {
            return [];
        }

        var results = new List<SearchResult>(request.MaxResults);
        foreach (var agent in SearchableAgents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileSystem.Directory.Exists(agent.WorkspacePath)
                || agent.Validator.ValidateAndResolve(agent.WorkspacePath, Abstractions.Security.FileAccessMode.Read) is null)
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = fileSystem.Directory.EnumerateFiles(
                    agent.WorkspacePath,
                    "*",
                    SearchOption.AllDirectories);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= request.MaxResults)
                {
                    return results;
                }

                if (IsGitMetadata(file, agent.WorkspacePath)
                    || agent.Validator.ValidateAndResolve(file, Abstractions.Security.FileAccessMode.Read) is null)
                {
                    continue;
                }

                var match = await TryMatchAsync(file, request.Query, cancellationToken).ConfigureAwait(false);
                if (match is null)
                {
                    continue;
                }

                var relativePath = fileSystem.Path.GetRelativePath(agent.WorkspacePath, file)
                    .Replace('\\', '/');
                results.Add(new SearchResult(
                    Title: relativePath,
                    Snippet: match,
                    Target: $"/files/{Uri.EscapeDataString(agent.AgentId)}/{EscapePath(relativePath)}",
                    Timestamp: fileSystem.File.GetLastWriteTimeUtc(file),
                    ProvenanceTrust: SearchProvenanceTrust.Untrusted));
            }
        }

        return results;
    }

    private IEnumerable<SearchableAgent> SearchableAgents()
    {
        foreach (var descriptor in agentRegistry.GetAll()
                     .OrderBy(agent => agent.AgentId.Value, StringComparer.OrdinalIgnoreCase))
        {
            var workspacePath = workspaceManager.GetWorkspacePath(descriptor.AgentId.Value);
            yield return new SearchableAgent(
                descriptor.AgentId.Value,
                workspacePath,
                new DefaultPathValidator(descriptor.FileAccess, workspacePath));
        }
    }

    private async Task<string?> TryMatchAsync(
        string file,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = fileSystem.FileInfo.New(file);
            if (fileInfo.Length > MaxFileBytes)
            {
                return FileNameMatches(file, query) ? fileInfo.Name : null;
            }

            await using var stream = fileSystem.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var probe = new byte[Math.Min(4096, checked((int)stream.Length))];
            var read = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            if (probe.AsSpan(0, read).Contains((byte)0))
            {
                return FileNameMatches(file, query) ? fileInfo.Name : null;
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchIndex = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (matchIndex >= 0)
                {
                    return TextTruncation.SafeTruncate(line.Trim(), MaxSnippetLength, "...") ?? string.Empty;
                }
            }

            return FileNameMatches(file, query) ? fileInfo.Name : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private bool FileNameMatches(string file, string query)
        => fileSystem.Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool IsGitMetadata(string file, string workspacePath)
    {
        var relative = Path.GetRelativePath(workspacePath, file);
        return relative.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relative.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePath(string relativePath)
        => string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    private sealed record SearchableAgent(
        string AgentId,
        string WorkspacePath,
        DefaultPathValidator Validator);
}
