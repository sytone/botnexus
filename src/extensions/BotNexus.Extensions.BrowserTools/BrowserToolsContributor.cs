using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// The shape <c>ExtensionConfig["botnexus-browser"]</c> deserialises into (#3031).
/// </summary>
/// <remarks>
/// Separate from <see cref="BrowserToolsConfig"/> because the two answer different questions.
/// This is the wire shape an operator writes; <see cref="BrowserToolsConfig"/> is the validated
/// value the guards read. Collapsing them would put JSON attribute noise on a type the security
/// code depends on, and would make every guard default a wire-format decision.
/// </remarks>
public sealed class BrowserToolsExtensionConfig
{
    /// <summary>Explicit path to an agent-browser executable.</summary>
    [JsonPropertyName("binaryPath")]
    public string? BinaryPath { get; init; }

    /// <summary>Exact agent-browser version for the managed install directory.</summary>
    [JsonPropertyName("pinnedVersion")]
    public string? PinnedVersion { get; init; }

    /// <summary>Whether the resolver may download the pinned release asset.</summary>
    [JsonPropertyName("autoProvision")]
    public bool AutoProvision { get; init; }

    /// <summary>Per-command timeout in seconds.</summary>
    [JsonPropertyName("commandTimeoutSeconds")]
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Maximum characters of page text returned inline.</summary>
    [JsonPropertyName("snapshotMaxChars")]
    public int? SnapshotMaxChars { get; init; }

    /// <summary>Extra blocked hostnames, on top of the shared SSRF policy.</summary>
    [JsonPropertyName("additionalBlockedHosts")]
    public IReadOnlyList<string>? AdditionalBlockedHosts { get; init; }

    /// <summary>Projects the wire shape onto the guard configuration.</summary>
    public BrowserToolsConfig ToBrowserToolsConfig() => new()
    {
        BinaryPath = BinaryPath,
        PinnedVersion = string.IsNullOrWhiteSpace(PinnedVersion)
            ? BrowserToolsConfig.DefaultPinnedVersion
            : PinnedVersion,
        AutoProvision = AutoProvision,
        CommandTimeoutSeconds = CommandTimeoutSeconds is > 0
            ? CommandTimeoutSeconds.Value
            : BrowserToolsConfig.DefaultCommandTimeoutSeconds,
        SnapshotMaxChars = SnapshotMaxChars is > 0
            ? SnapshotMaxChars.Value
            : BrowserToolsConfig.DefaultSnapshotMaxChars,
        AdditionalBlockedHosts = AdditionalBlockedHosts ?? [],
    };
}

/// <summary>
/// Contributes the five browser tools from per-agent extension configuration (#3031 AC1).
/// </summary>
/// <remarks>
/// <para>
/// Absence of <c>botnexus-browser</c> from the descriptor's <c>ExtensionConfig</c> contributes
/// ZERO tools, and does so before any binary is resolved or any environment is built. This is the
/// permissioning boundary for the whole feature: an agent that was never granted browser access
/// must not be able to reach a browser through a tool it can see but is told not to use, because
/// the thing a prompt-injected page asks for first is exactly that tool.
/// </para>
/// <para>
/// The session is built lazily. Contribution happens on every handle creation, and resolving a
/// binary - possibly provisioning one - at that point would make every agent start pay for a
/// capability most turns never use.
/// </para>
/// </remarks>
public sealed class BrowserToolsContributor : IAgentToolContributor
{
    /// <summary>The extension id this contributor answers to.</summary>
    public const string ExtensionId = "botnexus-browser";

    private readonly IAgentBrowserProcessRunner? _processRunner;
    private readonly IBrowserFileSystem? _fileSystem;
    private readonly Func<BrowserToolsConfig, Task<AgentBrowserResolution>>? _resolver;
    private readonly Func<string, string?>? _readParentVariable;

    /// <summary>Creates a contributor.</summary>
    /// <param name="processRunner">Process seam; <c>null</c> uses the real one.</param>
    /// <param name="fileSystem">Filesystem seam; <c>null</c> uses the real one.</param>
    /// <param name="resolver">Binary resolution override; <c>null</c> uses <see cref="AgentBrowserBinaryResolver"/>.</param>
    /// <param name="readParentVariable">Parent-environment reader for the child allow-list.</param>
    public BrowserToolsContributor(
        IAgentBrowserProcessRunner? processRunner = null,
        IBrowserFileSystem? fileSystem = null,
        Func<BrowserToolsConfig, Task<AgentBrowserResolution>>? resolver = null,
        Func<string, string?>? readParentVariable = null)
    {
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        _resolver = resolver;
        _readParentVariable = readParentVariable;
    }

    /// <summary>
    /// Every session created by this contributor, keyed by the agent session key it was built for.
    /// </summary>
    /// <remarks>
    /// Exposed so AC7's "two distinct session keys produce two distinct --session arguments" is
    /// asserted against what the contributor actually built, rather than against a re-derivation
    /// of <see cref="AgentBrowserCli.ToSessionId"/> inside the test.
    /// </remarks>
    public IReadOnlyDictionary<string, AgentBrowserCli> Drivers => _drivers;

    private readonly ConcurrentDictionary<string, AgentBrowserCli> _drivers =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var extensionConfig = ResolveExtensionConfig(context.Descriptor);
        if (extensionConfig is null)
        {
            // AC1. Nothing is resolved, nothing is launched, no environment is built.
            return Task.FromResult(new AgentToolContribution([]));
        }

        var config = extensionConfig.ToBrowserToolsConfig();
        var sessionKey = ResolveSessionKey(context);
        var sessionId = AgentBrowserCli.ToSessionId(sessionKey);

        // Guard initialisation is a VALUE, not an exception (#3030 AC4). If anything here throws,
        // the session that results denies every call rather than proceeding unguarded.
        var guardState = BrowserGuardState.Initialise(() =>
            BrowserToolsUrlGuard.Validate("https://example.invalid/", config));

        var fileSystem = _fileSystem ?? new BrowserFileSystem();
        BrowserToolSession? session = null;
        var sessionLock = new SemaphoreSlim(1, 1);

        async Task<BrowserToolSession> SessionFactory(CancellationToken ct)
        {
            if (session is not null)
            {
                return session;
            }

            await sessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (session is not null)
                {
                    return session;
                }

                var resolution = _resolver is not null
                    ? await _resolver(config).ConfigureAwait(false)
                    : await new AgentBrowserBinaryResolver(config, fileSystem)
                        .ResolveAsync(ct).ConfigureAwait(false);

                var cli = new AgentBrowserCli(
                    sessionId, resolution, _processRunner, config, _readParentVariable);

                _drivers[sessionKey] = cli;

                session = new BrowserToolSession(
                    new GuardedBrowserSession(
                        cli, context.WorkspacePath, config, guardState, fileSystem),
                    cli,
                    context.WorkspacePath,
                    fileSystem,
                    cli);

                return session;
            }
            finally
            {
                sessionLock.Release();
            }
        }

        // AC2: exactly these five names, in one place, so the set is countable by a test.
        var tools = new List<IAgentTool>
        {
            new BrowserNavigateTool(SessionFactory),
            new BrowserSnapshotTool(SessionFactory),
            new BrowserClickTool(SessionFactory),
            new BrowserTypeTool(SessionFactory),
            new BrowserScreenshotTool(SessionFactory),
        };

        // The session is registered for disposal even though it may never be created: a disposer
        // over a null session is free, whereas a browser left running because the tool was used
        // once and never registered is a leaked Chrome per agent handle.
        return Task.FromResult(new AgentToolContribution(
            tools,
            [new BrowserSessionDisposer(() => session)]));
    }

    /// <summary>
    /// Derives the session key from the contribution context (#3031 AC7).
    /// </summary>
    /// <remarks>
    /// Agent id AND session id, never one alone. Keying on the agent would let two concurrent
    /// sessions of the same agent share one browser and one cookie jar; keying on the session
    /// alone would collide across agents if a session id were ever reused.
    /// </remarks>
    public static string ResolveSessionKey(AgentToolContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AgentSessionKey
            .From(context.Descriptor.AgentId, context.ExecutionContext.SessionId)
            .ToString();
    }

    private static BrowserToolsExtensionConfig? ResolveExtensionConfig(AgentDescriptor descriptor)
    {
        if (!descriptor.ExtensionConfig.TryGetValue(ExtensionId, out var element))
        {
            return null;
        }

        try
        {
            // An empty object is a valid grant that takes every default; only a MISSING key means
            // "not granted". Returning null for `{}` would make an operator's minimal opt-in
            // silently do nothing.
            return JsonSerializer.Deserialize<BrowserToolsExtensionConfig>(element.GetRawText())
                ?? new BrowserToolsExtensionConfig();
        }
        catch (JsonException)
        {
            // Malformed config is treated as absent. Falling back to DEFAULTS here would grant
            // browser access off the back of a typo.
            return null;
        }
    }
}

/// <summary>
/// Disposes the lazily-created browser session with the agent handle (#3031 AC7).
/// </summary>
/// <remarks>
/// Indirected through a callback because the session may not exist yet when the contribution is
/// built, and may never exist. Capturing the session directly would capture <c>null</c> forever.
/// </remarks>
public sealed class BrowserSessionDisposer(Func<BrowserToolSession?> session) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var target = session();
        if (target is not null)
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }
    }
}
