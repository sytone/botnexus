namespace BotNexus.Gateway.A2A;

/// <summary>Classifies the bounded end of one remote A2A delegation without collapsing policy or transport failures.</summary>
public enum A2ATerminalOutcome
{
    /// <summary>The remote task produced its terminal result.</summary>
    Completed,
    /// <summary>The remote agent requires caller input or authority before it can continue.</summary>
    InputRequired,
    /// <summary>The remote agent reported an execution failure.</summary>
    Failed,
    /// <summary>The caller or remote agent cancelled the task.</summary>
    Cancelled,
    /// <summary>The caller deadline elapsed before a terminal result was available.</summary>
    DeadlineExpired,
    /// <summary>The remote agent rejected the task at a policy boundary.</summary>
    PolicyBlocked,
    /// <summary>Discovery, negotiation, protocol parsing, or HTTP transport failed.</summary>
    TransportFailed
}

/// <summary>Allows a provider-neutral caller to attach authentication after destination policy accepts a request.</summary>
/// <param name="request">The validated request that may be decorated with authentication material.</param>
/// <param name="cancellationToken">Cancels authentication when the caller or explicit deadline cancels the operation.</param>
public delegate ValueTask A2ARequestAuthenticator(
    HttpRequestMessage request,
    CancellationToken cancellationToken);

/// <summary>Supplies the operator-approved origin and explicit bounds for one A2A service.</summary>
public sealed record A2AClientOptions
{
    /// <summary>Default upper bound for an agent card, preventing discovery from becoming an unbounded read.</summary>
    public const int DefaultMaxAgentCardBytes = 64 * 1024;
    /// <summary>Default upper bound for a synchronous task response.</summary>
    public const int DefaultMaxResponseBytes = 1024 * 1024;
    /// <summary>Default upper bound for the serialized JSON-RPC submission.</summary>
    public const int DefaultMaxRequestBytes = 64 * 1024;
    /// <summary>Default character bound for the objective text before UTF-8 serialization.</summary>
    public const int DefaultMaxMessageTextCharacters = 32 * 1024;
    /// <summary>Default character bound for an optional opaque context identifier.</summary>
    public const int DefaultMaxContextIdCharacters = 1024;
    /// <summary>Default character bound for the compact model-visible terminal summary.</summary>
    public const int DefaultMaxSummaryCharacters = 4096;

    /// <summary>Creates policy for a true HTTP(S) origin root, excluding paths, credentials, query, and fragments.</summary>
    public A2AClientOptions(Uri approvedOrigin)
    {
        ArgumentNullException.ThrowIfNull(approvedOrigin);
        if (!approvedOrigin.IsAbsoluteUri
            || (approvedOrigin.Scheme != Uri.UriSchemeHttp && approvedOrigin.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(approvedOrigin.UserInfo)
            || approvedOrigin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(approvedOrigin.Query)
            || !string.IsNullOrEmpty(approvedOrigin.Fragment))
        {
            throw new ArgumentException(
                "The approved A2A origin must be an HTTP(S) origin root without user information, path, query, or fragment.",
                nameof(approvedOrigin));
        }

        ApprovedOrigin = approvedOrigin;
    }

    /// <summary>The only origin from which cards and negotiated task interfaces may be read.</summary>
    public Uri ApprovedOrigin { get; }
    /// <summary>Maximum accepted UTF-8 agent-card bytes.</summary>
    public int MaxAgentCardBytes { get; init; } = DefaultMaxAgentCardBytes;
    /// <summary>Maximum accepted UTF-8 synchronous response bytes.</summary>
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
    /// <summary>Maximum serialized UTF-8 bytes permitted for one outbound JSON-RPC request.</summary>
    public int MaxRequestBytes { get; init; } = DefaultMaxRequestBytes;
    /// <summary>Maximum objective text characters permitted before any discovery request is made.</summary>
    public int MaxMessageTextCharacters { get; init; } = DefaultMaxMessageTextCharacters;
    /// <summary>Maximum optional context identifier characters permitted before any discovery request is made.</summary>
    public int MaxContextIdCharacters { get; init; } = DefaultMaxContextIdCharacters;
    /// <summary>Maximum characters copied from a remote text part into the compact terminal summary.</summary>
    public int MaxSummaryCharacters { get; init; } = DefaultMaxSummaryCharacters;
    /// <summary>Additional operator-blocked hosts passed to the shared SSRF policy.</summary>
    public IReadOnlyList<string>? AdditionalBlockedHosts { get; init; }
    /// <summary>Optionally decorates the validated public agent-card request with provider-neutral authentication.</summary>
    public A2ARequestAuthenticator? AuthenticateDiscoveryAsync { get; init; }
    /// <summary>Optionally decorates the separately validated JSON-RPC submission with provider-neutral authentication.</summary>
    public A2ARequestAuthenticator? AuthenticateSubmissionAsync { get; init; }
}

/// <summary>Contains the bounded user assignment sent as one A2A message.</summary>
public sealed record A2AMessage(string Text, string? ContextId = null);
/// <summary>Describes the validated agent and negotiated synchronous JSON-RPC interface.</summary>
public sealed record A2ADiscoveryResult(string AgentName, string ProtocolVersion, Uri AgentCardUri, Uri Endpoint);
/// <summary>References an artifact without copying its potentially large bytes into the immediate result.</summary>
public sealed record A2AArtifactReference(string? Id, string? Name, Uri Uri, string? MediaType);
/// <summary>Identifies the untrusted remote source that produced a result.</summary>
public sealed record A2AProvenance(string AgentName, Uri AgentCardUri, Uri Endpoint, string ProtocolVersion);
/// <summary>Reports remote usage only when the remote service supplied it.</summary>
public sealed record A2AUsage(long? InputTokens, long? OutputTokens);
/// <summary>Returns one compact terminal task projection while preserving remote identity and provenance.</summary>
public sealed record A2ATaskResult(
    A2ATerminalOutcome Outcome,
    string? ContextId,
    string? TaskId,
    string? Summary,
    IReadOnlyList<A2AArtifactReference> Artifacts,
    A2AProvenance? Provenance,
    A2AUsage? Usage,
    string? Error);

/// <summary>Signals a fail-closed agent-card or A2A wire-contract violation.</summary>
public sealed class A2AProtocolException : Exception
{
    /// <summary>Creates a protocol failure suitable for projection as transport-failed.</summary>
    public A2AProtocolException(string message) : base(message) { }
    /// <summary>Creates a protocol failure with its bounded parsing or transport cause.</summary>
    public A2AProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
