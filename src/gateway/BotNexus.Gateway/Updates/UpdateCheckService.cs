using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Gateway.Updates;

/// <summary>
/// Background service that periodically polls GitHub for new commits and caches the result.
/// Exposes the cached state synchronously for portal/controller consumption.
/// Also handles validating prerequisites and spawning the CLI update process.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService, IHostedService, IDisposable
{
    private readonly IOptions<PlatformConfig> _config;
    private readonly HttpClient _httpClient;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    // Cached status — updated by CheckNowAsync or constructor initialisation.
    private volatile UpdateStatusResult _status;

    // Guards against concurrent update spawns.
    private int _updateInProgress;

    private PeriodicTimer? _timer;
    private Task? _timerTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Environment variable name that can override the commit SHA resolved from assembly metadata.
    /// Used in test environments where the assembly is not built with SourceRevisionId.
    /// </summary>
    internal const string CommitShaOverrideEnvVar = "BOTNEXUS_COMMIT_SHA";

    public UpdateCheckService(
        IOptions<PlatformConfig> config,
        HttpClient httpClient,
        IFileSystem fileSystem,
        ILogger<UpdateCheckService> logger,
        IHostApplicationLifetime lifetime)
    {
        _config = config;
        _httpClient = httpClient;
        _fileSystem = fileSystem;
        _logger = logger;
        _lifetime = lifetime;

        // Build initial cached status synchronously — no GitHub call.
        _status = BuildInitialStatus();
    }

    /// <inheritdoc/>
    public UpdateStatusResult GetCurrentStatus() => _status;

    /// <inheritdoc/>
    public async Task<UpdateStatusResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        var cfg = GetAutoUpdateConfig();

        try
        {
            var url = $"https://api.github.com/repos/{cfg.RepositoryOwner}/{cfg.RepositoryName}/commits/{cfg.Branch}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "BotNexus/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var latestSha = doc.RootElement.GetProperty("sha").GetString() ?? string.Empty;

            var currentSha = ResolveCurrentCommitSha();
            var isUpdateAvailable = !string.IsNullOrEmpty(latestSha)
                && !string.IsNullOrEmpty(currentSha)
                && currentSha != "unknown"
                && !string.Equals(currentSha, latestSha, StringComparison.OrdinalIgnoreCase);

            var now = DateTimeOffset.UtcNow;
            var compareUrl = isUpdateAvailable
                ? $"https://github.com/{cfg.RepositoryOwner}/{cfg.RepositoryName}/compare/{ShortSha(currentSha)}...{ShortSha(latestSha)}"
                : null;

            _status = new UpdateStatusResult(
                Enabled: cfg.Enabled,
                IsChecking: false,
                IsUpdateAvailable: isUpdateAvailable,
                IsUpdateInProgress: _updateInProgress == 1,
                CurrentCommitSha: currentSha,
                CurrentCommitShort: ShortSha(currentSha),
                LatestCommitSha: latestSha,
                LatestCommitShort: ShortSha(latestSha),
                LastCheckedAt: now,
                NextCheckAt: now.AddMinutes(cfg.CheckIntervalMinutes),
                RepositoryOwner: cfg.RepositoryOwner,
                RepositoryName: cfg.RepositoryName,
                Branch: cfg.Branch,
                CompareUrl: compareUrl,
                Error: null);

            if (isUpdateAvailable)
                _logger.LogInformation("Update available: {LatestSha} (current: {CurrentSha})", ShortSha(latestSha), ShortSha(currentSha));
            else
                _logger.LogDebug("No update available. Current: {CurrentSha}", ShortSha(currentSha));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates.");
            var currentSha = ResolveCurrentCommitSha();
            _status = _status with
            {
                Error = ex.Message,
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        return _status;
    }

    /// <inheritdoc/>
    public async Task<UpdateStartResult> StartUpdateAsync(CancellationToken cancellationToken = default)
    {
        var cfg = GetAutoUpdateConfig();

        // Prerequisite: auto-update enabled
        if (!cfg.Enabled)
            return new UpdateStartResult(false, null, "412: Auto-update is not enabled.");

        // Prerequisite: CliPath required
        if (string.IsNullOrWhiteSpace(cfg.CliPath))
            return new UpdateStartResult(false, null, "412: CliPath is not configured.");

        // Prerequisite: SourcePath required
        if (string.IsNullOrWhiteSpace(cfg.SourcePath))
            return new UpdateStartResult(false, null, "412: SourcePath is not configured.");

        // Prerequisite: no update in progress
        if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0)
            return new UpdateStartResult(false, null, "409: Update already in progress.");

        // If we've already checked and there's no update, don't spawn.
        // Skip this gate if we haven't checked yet (LastCheckedAt == null) so the process can be forced.
        if (_status.LastCheckedAt.HasValue && !_status.IsUpdateAvailable)
        {
            Interlocked.Exchange(ref _updateInProgress, 0);
            return new UpdateStartResult(false, null, "412: No update is available.");
        }

        try
        {
            var targetPath = BotNexusHome.ResolveHomePath();
            var port = ResolvePort();

            var cliPath = cfg.CliPath;
            var sourcePath = cfg.SourcePath;

            // Append --channel only when a non-empty channel is configured.
            var channelArg = !string.IsNullOrWhiteSpace(cfg.Channel)
                ? $" --channel {cfg.Channel}"
                : string.Empty;

            ProcessStartInfo startInfo;
            if (cliPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo("dotnet", $"\"{cliPath}\" update --source \"{sourcePath}\" --target \"{targetPath}\" --port {port}{channelArg}")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }
            else
            {
                startInfo = new ProcessStartInfo($"\"{cliPath}\"", $"update --source \"{sourcePath}\" --target \"{targetPath}\" --port {port}{channelArg}")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                Interlocked.Exchange(ref _updateInProgress, 0);
                return new UpdateStartResult(false, null, "412: Failed to start update process.");
            }

            // Schedule graceful shutdown after delay so response is sent first.
            var delay = cfg.ShutdownDelaySeconds;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), CancellationToken.None);
                _lifetime.StopApplication();
            }, CancellationToken.None);

            return new UpdateStartResult(true, process.Id, "Update started");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _updateInProgress, 0);
            _logger.LogError(ex, "Failed to start update process.");
            return new UpdateStartResult(false, null, $"412: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = GetAutoUpdateConfig();
        if (!cfg.Enabled)
            return;

        // Run initial check immediately.
        await CheckNowAsync(cancellationToken);

        // Start periodic background polling.
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(cfg.CheckIntervalMinutes));
        _timerTask = RunTimerLoopAsync(_cts.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_timerTask != null)
        {
            try { await _timerTask; } catch (OperationCanceledException) { }
            _timerTask = null;
        }

        _timer?.Dispose();
        _timer = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────

    private async Task RunTimerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
                await CheckNowAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private UpdateStatusResult BuildInitialStatus()
    {
        var cfg = GetAutoUpdateConfig();
        var currentSha = ResolveCurrentCommitSha();
        return new UpdateStatusResult(
            Enabled: cfg.Enabled,
            IsChecking: false,
            IsUpdateAvailable: false,
            IsUpdateInProgress: false,
            CurrentCommitSha: currentSha,
            CurrentCommitShort: ShortSha(currentSha),
            LatestCommitSha: null,
            LatestCommitShort: null,
            LastCheckedAt: null,
            NextCheckAt: null,
            RepositoryOwner: cfg.RepositoryOwner,
            RepositoryName: cfg.RepositoryName,
            Branch: cfg.Branch,
            CompareUrl: null,
            Error: null);
    }

    private AutoUpdateConfig GetAutoUpdateConfig() =>
        _config.Value.Gateway?.AutoUpdate ?? new AutoUpdateConfig();

    private static string ResolveCurrentCommitSha()
    {
        // Allow test/environment override via environment variable.
        var envSha = Environment.GetEnvironmentVariable(CommitShaOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(envSha))
            return envSha.ToLowerInvariant();

        // Search all loaded assemblies for the CommitSha AssemblyMetadataAttribute.
        // The attribute is embedded by BotNexus.Gateway.Api at build time.
        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var attr = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "CommitSha");
            if (attr?.Value is { Length: > 0 } sha && sha != "unknown")
                return sha.ToLowerInvariant();
        }

        return "unknown";
    }

    private int ResolvePort()
    {
        var listenUrl = _config.Value.Gateway?.ListenUrl;
        if (!string.IsNullOrWhiteSpace(listenUrl) && Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri))
            return uri.Port > 0 ? uri.Port : 5005;
        return 5005;
    }

    /// <summary>
    /// Builds the argument string for the CLI update command.
    /// Exposed as internal so tests can assert the --channel flag is forwarded correctly.
    /// </summary>
    internal static string BuildUpdateArguments(string sourcePath, string targetPath, int port, string? channel)
    {
        var channelArg = !string.IsNullOrWhiteSpace(channel)
            ? $" --channel {channel}"
            : string.Empty;
        return $"update --source \"{sourcePath}\" --target \"{targetPath}\" --port {port}{channelArg}";
    }

    private static string ShortSha(string? sha) =>
        sha?.Length >= 7 ? sha[..7] : sha ?? string.Empty;
}
