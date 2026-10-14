using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Api.ReleaseHistory;

/// <summary>Reads canonical release metadata and bounded Git distance from the local BotNexus checkout.</summary>
public sealed class LocalReleaseHistoryService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ManifestPath = "docs/public/releases/release-history.json";
    private readonly IOptions<PlatformConfig> _config;
    private readonly ILogger<LocalReleaseHistoryService> _logger;
    private readonly string _runningCommit;

    /// <summary>Creates a local release-history reader using the running assembly identity.</summary>
    public LocalReleaseHistoryService(
        IOptions<PlatformConfig> config,
        ILogger<LocalReleaseHistoryService> logger)
        : this(config, logger, GatewayBuildInfo.CommitSha)
    {
    }

    /// <summary>Creates a local release-history reader with an explicit running commit identity.</summary>
    public LocalReleaseHistoryService(
        IOptions<PlatformConfig> config,
        ILogger<LocalReleaseHistoryService> logger,
        string runningCommit)
    {
        _config = config;
        _logger = logger;
        _runningCommit = runningCommit;
    }

    /// <summary>Returns locally available release history and optionally refreshes remote-tracking refs.</summary>
    public async Task<object> GetAsync(bool refreshRemote, CancellationToken cancellationToken)
    {
        var sourcePath = ResolveSourcePath(_config.Value.Gateway?.AutoUpdate?.SourcePath);
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException("The BotNexus source checkout is unavailable.");

        var branch = _config.Value.Gateway?.AutoUpdate?.Branch;
        if (string.IsNullOrWhiteSpace(branch))
            branch = "main";
        var remoteRef = $"origin/{branch}";

        string? refreshError = null;
        if (refreshRemote)
        {
            var fetch = await RunGitAsync(
                sourcePath,
                ["fetch", "--quiet", "--tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
                cancellationToken);
            if (!fetch.Succeeded)
            {
                refreshError = "Remote refresh failed; showing locally cached Git data.";
                _logger.LogWarning("Release history Git refresh failed for {SourcePath}: {Error}", sourcePath, fetch.Error);
            }
        }

        var checkoutHead = await RequireGitOutputAsync(sourcePath, ["rev-parse", "HEAD"], cancellationToken);
        var runningCommit = _runningCommit;
        var manifestJson = await RequireGitOutputAsync(sourcePath, ["show", $"HEAD:{ManifestPath}"], cancellationToken);
        var manifest = JsonSerializer.Deserialize<ReleaseHistoryManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidDataException("The local release history manifest is empty.");
        ValidateManifest(manifest);

        var latestRelease = manifest.Releases.FirstOrDefault();
        var releaseDistance = latestRelease is null
            ? null
            : await ReadDistanceAsync(sourcePath, runningCommit, latestRelease.Commit, cancellationToken);
        var remoteDistance = await ReadDistanceAsync(sourcePath, runningCommit, remoteRef, cancellationToken);

        return new ReleaseHistoryResponse(
            manifest.SchemaVersion,
            manifest.Releases,
            new SourceVersionStatus(
                RunningCommit: runningCommit,
                RunningCommitShort: ShortSha(runningCommit),
                CheckoutHead: checkoutHead,
                CheckoutHeadShort: ShortSha(checkoutHead),
                LatestReleaseVersion: latestRelease?.Version,
                LatestReleaseCommit: latestRelease?.Commit,
                ReleaseDistance: releaseDistance,
                RemoteName: "origin",
                RemoteBranch: branch,
                RemoteHead: remoteDistance?.TargetCommit,
                RemoteDistance: remoteDistance,
                RemoteRefreshError: refreshError));
    }

    internal static string ResolveSourcePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "botnexus");
    }

    private static async Task<GitDistance?> ReadDistanceAsync(
        string sourcePath,
        string sourceCommit,
        string target,
        CancellationToken cancellationToken)
    {
        if (!IsCommitSha(sourceCommit) ||
            !await GitObjectExistsAsync(sourcePath, sourceCommit, cancellationToken) ||
            !await GitObjectExistsAsync(sourcePath, target, cancellationToken))
        {
            return null;
        }

        var targetCommit = await RequireGitOutputAsync(sourcePath, ["rev-parse", target], cancellationToken);
        var counts = await RequireGitOutputAsync(
            sourcePath,
            ["rev-list", "--left-right", "--count", $"{sourceCommit}...{targetCommit}"],
            cancellationToken);
        var parts = counts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var ahead) || !int.TryParse(parts[1], out var behind))
            return null;
        return new GitDistance(targetCommit, ShortSha(targetCommit), ahead, behind);
    }

    private static async Task<bool> GitObjectExistsAsync(string sourcePath, string value, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(sourcePath, ["cat-file", "-e", value], cancellationToken);
        return result.Succeeded;
    }

    private static async Task<string> RequireGitOutputAsync(
        string sourcePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(sourcePath, arguments, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidDataException("The BotNexus source checkout does not contain usable release history.");
        return result.Output.Trim();
    }

    private static async Task<GitResult> RunGitAsync(
        string sourcePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = sourcePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.interactive=never");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return new(false, string.Empty, "Git could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode == 0, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new(false, string.Empty, "Git command timed out.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, string.Empty, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ValidateManifest(ReleaseHistoryManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, "1.0.0", StringComparison.Ordinal))
            throw new InvalidDataException("The local release history format is unsupported.");
        if (manifest.Releases.Any(release =>
                string.IsNullOrWhiteSpace(release.Version) ||
                !string.Equals(release.Tag, $"v{release.Version}", StringComparison.Ordinal) ||
                !IsCommitSha(release.Commit) ||
                !DateOnly.TryParseExact(release.ReleasedAt, "yyyy-MM-dd", out _) ||
                !IsPublicUri(release.ReleaseUrl) ||
                !IsPublicUri(release.DocumentationUrl) ||
                release.Categories.Any(category =>
                    string.IsNullOrWhiteSpace(category.Name) ||
                    category.Changes.Any(change =>
                        string.IsNullOrWhiteSpace(change.Summary) ||
                        change.DocumentationUrls.Any(url => !IsPublicUri(url))))))
        {
            throw new InvalidDataException("The local release history contains invalid release metadata.");
        }
    }

    private static bool IsPublicUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return (uri.Host.Equals("sytone.github.io", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/botnexus/", StringComparison.OrdinalIgnoreCase)) ||
               (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/Sytone/botnexus/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommitSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string ShortSha(string value) => value.Length >= 7 ? value[..7] : value;

    private sealed record GitResult(bool Succeeded, string Output, string Error);
}

internal sealed record ReleaseHistoryResponse(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseHistoryEntry> Releases,
    [property: JsonPropertyName("sourceStatus")] SourceVersionStatus SourceStatus);

internal sealed record ReleaseHistoryManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("releases")] IReadOnlyList<ReleaseHistoryEntry> Releases);

internal sealed record ReleaseHistoryEntry(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("releasedAt")] string ReleasedAt,
    [property: JsonPropertyName("releaseUrl")] string ReleaseUrl,
    [property: JsonPropertyName("documentationUrl")] string DocumentationUrl,
    [property: JsonPropertyName("categories")] IReadOnlyList<ReleaseChangeCategory> Categories);

internal sealed record ReleaseChangeCategory(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("changes")] IReadOnlyList<ReleaseChange> Changes);

internal sealed record ReleaseChange(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("documentationUrls")] IReadOnlyList<string> DocumentationUrls);

internal sealed record SourceVersionStatus(
    string RunningCommit,
    string RunningCommitShort,
    string CheckoutHead,
    string CheckoutHeadShort,
    string? LatestReleaseVersion,
    string? LatestReleaseCommit,
    GitDistance? ReleaseDistance,
    string RemoteName,
    string RemoteBranch,
    string? RemoteHead,
    GitDistance? RemoteDistance,
    string? RemoteRefreshError);

internal sealed record GitDistance(string TargetCommit, string TargetCommitShort, int Ahead, int Behind);
