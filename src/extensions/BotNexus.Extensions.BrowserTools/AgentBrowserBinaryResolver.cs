using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>Which step of the fixed resolution order produced the binary (#3029 AC5).</summary>
public enum AgentBrowserSource
{
    /// <summary>No binary was found and none could be provisioned.</summary>
    NotFound = 0,

    /// <summary>Step 1: an explicit <c>browser.binaryPath</c> from configuration.</summary>
    ConfiguredPath,

    /// <summary>Step 2: the managed directory <c>~/.botnexus/tools/agent-browser/&lt;version&gt;/</c>.</summary>
    ManagedDirectory,

    /// <summary>Step 3: <c>agent-browser</c> discovered on <c>PATH</c>.</summary>
    Path,

    /// <summary>Step 4: downloaded from the pinned release asset and sha256-verified.</summary>
    Provisioned,
}

/// <summary>
/// Outcome of resolving the <c>agent-browser</c> binary. Never throws for "not found" (AC6).
/// </summary>
/// <param name="Source">Which step succeeded, or <see cref="AgentBrowserSource.NotFound"/>.</param>
/// <param name="BinaryPath">Absolute path to the executable; <c>null</c> when not resolved.</param>
/// <param name="Message">
/// Operator-facing explanation. Populated on failure with concrete install options; may be
/// <c>null</c> on success.
/// </param>
public sealed record AgentBrowserResolution(
    AgentBrowserSource Source,
    string? BinaryPath,
    string? Message)
{
    /// <summary>Whether a usable binary path was produced.</summary>
    public bool IsResolved => Source != AgentBrowserSource.NotFound && BinaryPath is not null;
}

/// <summary>
/// Locates the <c>agent-browser</c> executable in a fixed, documented order (#3029).
/// </summary>
/// <remarks>
/// <para>
/// The order is config path, then the managed directory, then <c>PATH</c>, then — and only when
/// <c>browser.autoProvision</c> is explicitly <c>true</c> — a download of the pinned release
/// asset. Auto-provision is last and off by default because it is the only step that reaches the
/// network, and a resolver that silently downloads an executable on first use is a supply-chain
/// decision being made by a default value rather than by an operator.
/// </para>
/// <para>
/// Failure is a returned value, not an exception (AC6). "The browser tool is unavailable, here is
/// how to install it" is ordinary operating information that the agent should be able to relay;
/// an exception here would surface as a crash in a tool the operator never enabled.
/// </para>
/// </remarks>
public sealed class AgentBrowserBinaryResolver
{
    /// <summary>Executable name probed on <c>PATH</c> and inside the managed directory.</summary>
    public const string ExecutableName = "agent-browser";

    private readonly BrowserToolsConfig _config;
    private readonly IBrowserFileSystem _fileSystem;
    private readonly AgentBrowserReleaseCatalog _catalog;
    private readonly Func<HttpClient>? _httpClientFactory;
    private readonly Func<string?> _pathVariable;
    private readonly Func<string> _homeDirectory;
    private readonly string _runtimeIdentifier;
    private readonly bool _isWindows;

    /// <summary>Creates a resolver.</summary>
    /// <param name="config">Browser tool configuration; <c>null</c> uses defaults.</param>
    /// <param name="fileSystem">Filesystem seam; <c>null</c> uses the real filesystem.</param>
    /// <param name="catalog">Pinned release assets; <c>null</c> uses the (empty) default catalogue.</param>
    /// <param name="httpClientFactory">
    /// Supplies the client used for provisioning. Injectable so tests can install a handler that
    /// FAILS if it is ever called — which is how AC7's "no network call" is asserted rather than
    /// assumed.
    /// </param>
    /// <param name="pathVariable">Reads <c>PATH</c>; injectable to keep probing off the real host.</param>
    /// <param name="homeDirectory">Resolves the user profile root for the managed directory.</param>
    /// <param name="runtimeIdentifier">RID used for catalogue lookup, e.g. <c>win-x64</c>.</param>
    /// <param name="isWindows">
    /// Whether to probe Windows executable extensions. Defaults to the running OS; explicit so a
    /// Linux gate container can still exercise the Windows probing branch.
    /// </param>
    public AgentBrowserBinaryResolver(
        BrowserToolsConfig? config = null,
        IBrowserFileSystem? fileSystem = null,
        AgentBrowserReleaseCatalog? catalog = null,
        Func<HttpClient>? httpClientFactory = null,
        Func<string?>? pathVariable = null,
        Func<string>? homeDirectory = null,
        string? runtimeIdentifier = null,
        bool? isWindows = null)
    {
        _config = config ?? new BrowserToolsConfig();
        _fileSystem = fileSystem ?? new BrowserFileSystem();
        _catalog = catalog ?? AgentBrowserReleaseCatalog.Default;
        _httpClientFactory = httpClientFactory;
        _pathVariable = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _homeDirectory = homeDirectory
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _runtimeIdentifier = runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        _isWindows = isWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Runs the resolution order and returns the outcome. Does not throw when nothing is found.
    /// </summary>
    public async Task<AgentBrowserResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // ---- (1) explicit configuration ---------------------------------------------------
        // An operator who named a path meant it. If that path is wrong we say so rather than
        // quietly falling through to PATH, because a silent fallback would run a DIFFERENT
        // binary from the one that was configured and report success.
        if (!string.IsNullOrWhiteSpace(_config.BinaryPath))
        {
            if (_fileSystem.FileExists(_config.BinaryPath))
            {
                return new AgentBrowserResolution(
                    AgentBrowserSource.ConfiguredPath, _config.BinaryPath, null);
            }

            return NotFound(
                $"The configured browser.binaryPath '{_config.BinaryPath}' does not exist.");
        }

        // ---- (2) managed directory --------------------------------------------------------
        var managed = ManagedBinaryPath();
        if (_fileSystem.FileExists(managed))
        {
            return new AgentBrowserResolution(AgentBrowserSource.ManagedDirectory, managed, null);
        }

        // ---- (3) PATH ---------------------------------------------------------------------
        var onPath = ProbePath();
        if (onPath is not null)
        {
            return new AgentBrowserResolution(AgentBrowserSource.Path, onPath, null);
        }

        // ---- (4) provisioning, only on an explicit opt-in ---------------------------------
        if (!_config.AutoProvision)
        {
            // AC7: this return is the whole control. No HttpClient is constructed, no directory
            // is created, nothing is written. The failing-handler test proves the absence.
            return NotFound(
                "No agent-browser binary was found and browser.autoProvision is disabled.");
        }

        return await ProvisionAsync(managed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The managed install location for the pinned version:
    /// <c>~/.botnexus/tools/agent-browser/&lt;pinnedVersion&gt;/agent-browser[.exe]</c>.
    /// </summary>
    public string ManagedBinaryPath() => _fileSystem.CombinePath(
        ManagedVersionDirectory(), ExecutableName + (_isWindows ? ".exe" : string.Empty));

    private string ManagedVersionDirectory() => _fileSystem.CombinePath(
        _homeDirectory(), ".botnexus", "tools", ExecutableName, _config.PinnedVersion);

    private string? ProbePath()
    {
        var raw = _pathVariable();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // On Windows a bare name is only executable if it carries one of PATHEXT's extensions,
        // so probing for the extensionless name alone would miss every real Windows install.
        string[] candidates = _isWindows
            ? [ExecutableName + ".exe", ExecutableName + ".cmd", ExecutableName + ".bat", ExecutableName]
            : [ExecutableName];

        foreach (var dir in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = _fileSystem.CombinePath(dir, candidate);
                if (_fileSystem.FileExists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private async Task<AgentBrowserResolution> ProvisionAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var asset = _catalog.Find(_config.PinnedVersion, _runtimeIdentifier);
        if (asset is null)
        {
            // Fail closed rather than synthesise a download URL. A fetch whose expected digest is
            // unknown cannot be verified, and an unverified fetch is the thing AC8 forbids.
            return NotFound(
                $"No pinned agent-browser release is recorded for version '{_config.PinnedVersion}' "
                + $"on runtime '{_runtimeIdentifier}', so it cannot be downloaded and verified.");
        }

        if (_httpClientFactory is null)
        {
            return NotFound("Auto-provisioning is enabled but no HTTP client was supplied.");
        }

        byte[] payload;
        try
        {
            using var client = _httpClientFactory();
            using var response = await client
                .GetAsync(asset.AssetUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            payload = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return NotFound($"Downloading agent-browser from '{asset.AssetUrl}' failed: {ex.Message}");
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));

        _fileSystem.CreateDirectory(ManagedVersionDirectory());
        await _fileSystem.WriteAllBytesAsync(destination, payload, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Delete before returning (AC8). Leaving a mismatched executable on disk under the
            // managed path would let the NEXT resolve find it at step 2 and run it without any
            // check at all - the failed download would have installed itself one run later.
            _fileSystem.DeleteFile(destination);

            return NotFound(
                "The downloaded agent-browser binary failed sha256 verification "
                + $"(expected {asset.Sha256}, got {actual}); it has been deleted.");
        }

        return new AgentBrowserResolution(AgentBrowserSource.Provisioned, destination, null);
    }

    private static AgentBrowserResolution NotFound(string reason) =>
        new(AgentBrowserSource.NotFound, null, reason + " " + InstallGuidance);

    /// <summary>
    /// The actionable install guidance appended to every failure (AC6).
    /// </summary>
    /// <remarks>
    /// Naming the concrete commands matters: "agent-browser was not found" tells an operator
    /// something they already know, whereas the exact install line is the thing that turns a
    /// dead tool into a working one without a support round-trip.
    /// </remarks>
    public const string InstallGuidance =
        "Install agent-browser using one of: 'npm i -g agent-browser', "
        + "'brew install agent-browser', 'cargo install agent-browser', or download the binary "
        + "directly from the agent-browser GitHub releases page and set browser.binaryPath to it.";
}
