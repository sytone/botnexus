using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotNexus.Gateway.Abstractions.Workflows;

/// <summary>A code-registered activity identity and bounded JSON wire contract. Versions are immutable contracts.</summary>
public sealed record ActivityDescriptor
{
    /// <summary>Default UTF-8 JSON payload limit (64 KiB) for each direction.</summary>
    public const int DefaultMaxBytes = 64 * 1024;
    /// <summary>Largest permitted contract limit (1 MiB); also the engine bridge's input ceiling.</summary>
    public const int AbsoluteMaxBytes = 1024 * 1024;

    /// <summary>Creates an immutable, validated activity wire contract.</summary>
    /// <param name="name">Lowercase dotted namespace identifier, at most 128 characters, such as tasks.read.</param>
    /// <param name="version">Positive contract version; incompatible changes require a new version.</param>
    /// <param name="maxInputBytes">Maximum UTF-8 input size, from one byte through AbsoluteMaxBytes.</param>
    /// <param name="maxOutputBytes">Maximum UTF-8 output size, from one byte through AbsoluteMaxBytes.</param>
    /// <param name="inputKind">Optional defined top-level JSON kind; null permits any defined kind.</param>
    /// <param name="outputKind">Optional defined top-level JSON kind; null permits any defined kind.</param>
    /// <exception cref="ArgumentException">The name or a JSON kind is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version or a byte limit is outside its allowed range.</exception>
    public ActivityDescriptor(string name, int version, int maxInputBytes = DefaultMaxBytes,
        int maxOutputBytes = DefaultMaxBytes, JsonValueKind? inputKind = null, JsonValueKind? outputKind = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            !Regex.IsMatch(name, "^[a-z][a-z0-9_-]*(\\.[a-z][a-z0-9_-]*)+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Activity name must be a lowercase, namespaced identifier (for example tasks.read).", nameof(name));
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        if (maxInputBytes is < 1 or > AbsoluteMaxBytes) throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
        if (maxOutputBytes is < 1 or > AbsoluteMaxBytes) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        ValidateKind(inputKind, nameof(inputKind));
        ValidateKind(outputKind, nameof(outputKind));
        Name = name;
        Version = version;
        MaxInputBytes = maxInputBytes;
        MaxOutputBytes = maxOutputBytes;
        InputKind = inputKind;
        OutputKind = outputKind;
    }

    /// <summary>Gets the case-sensitive namespaced activity identifier.</summary>
    public string Name { get; }
    /// <summary>Gets the positive immutable wire-contract version.</summary>
    public int Version { get; }
    /// <summary>Gets the maximum UTF-8 byte count of the input JSON text.</summary>
    public int MaxInputBytes { get; }
    /// <summary>Gets the maximum UTF-8 byte count of the output JSON text.</summary>
    public int MaxOutputBytes { get; }
    /// <summary>Optional top-level wire shape. Activities own validation of domain-specific fields.</summary>
    public JsonValueKind? InputKind { get; }
    /// <summary>Gets the optional top-level output shape; activities validate domain-specific fields.</summary>
    public JsonValueKind? OutputKind { get; }

    private static void ValidateKind(JsonValueKind? kind, string parameter)
    {
        if (kind is { } value && (value == JsonValueKind.Undefined || !Enum.IsDefined(value)))
            throw new ArgumentException("A wire contract cannot use an undefined JSON kind.", parameter);
    }
}

/// <summary>Runtime-only authorization context. Never place this object in persisted workflow data.</summary>
public sealed record ActivityInvocationContext
{
    /// <summary>Creates a runtime identity binding without copying or persisting the principal.</summary>
    /// <param name="principal">Trusted current principal, not identity asserted in authored workflow data.</param>
    /// <param name="runId">Nonempty run identifier; the engine bridge requires the engine-generated instance ID.</param>
    /// <exception cref="ArgumentNullException">The principal is null.</exception>
    /// <exception cref="ArgumentException">The run identifier is empty or whitespace.</exception>
    public ActivityInvocationContext(ClaimsPrincipal principal, string runId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        Principal = principal;
        RunId = runId;
    }
    /// <summary>Gets the trusted principal reference; callers must not mutate it during authorization or invocation.</summary>
    public ClaimsPrincipal Principal { get; }
    /// <summary>Gets the trusted run binding, checked against the engine instance by the bridge.</summary>
    public string RunId { get; }
}

/// <summary>A trusted code-registered operation, resolved in the catalog's execution scope.</summary>
/// <remarks>
/// Scoped or transient implementations are disposed by the owning DI scope, not the catalog. Singleton implementations
/// must be thread-safe. Calls are not serialized, even within a scope. Effectful implementations require an idempotent
/// attempt/reconciliation boundary: engine suspension cannot prevent replay after a crash before checkpoint persistence.
/// </remarks>
public interface IWorkflowActivity
{
    /// <summary>Gets the immutable contract captured when the catalog is constructed.</summary>
    ActivityDescriptor Descriptor { get; }
    /// <summary>Executes an already authorized operation and validates its domain-specific input fields.</summary>
    /// <param name="context">Trusted runtime identity; never persist its principal or credentials.</param>
    /// <param name="input">Catalog-owned cloned input, independent of the caller's source document.</param>
    /// <param name="cancellationToken">Cooperative cancellation; cancellation does not imply effects were rolled back.</param>
    /// <returns>Defined JSON within the descriptor's output contract. Keep its backing document alive until the catalog clones it.</returns>
    ValueTask<JsonElement> InvokeAsync(ActivityInvocationContext context, JsonElement input, CancellationToken cancellationToken);
}

/// <summary>Checks current permissions for every invocation; the default implementation denies all calls.</summary>
/// <remarks>Resolved with the scoped catalog. Singleton policies must be thread-safe; never trust authored roles or cached resume-time grants.</remarks>
public interface IActivityAuthorizer
{
    /// <summary>Evaluates current authorization before payload validation or activity execution.</summary>
    /// <param name="activity">The registered immutable contract to authorize.</param>
    /// <param name="context">The trusted runtime principal and run binding.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>True only when current policy permits this invocation; false denies without calling the activity.</returns>
    ValueTask<bool> AuthorizeAsync(ActivityDescriptor activity, ActivityInvocationContext context, CancellationToken cancellationToken);
}

/// <summary>A scoped allowlist of explicitly registered activity contracts and authorized JSON invocations.</summary>
/// <remarks>Resolve inside an execution scope. No calls are serialized; dependency thread safety remains the contributor's responsibility.</remarks>
public interface IActivityCatalog
{
    /// <summary>Gets an immutable snapshot sorted by ordinal name and version.</summary>
    IReadOnlyCollection<ActivityDescriptor> Activities { get; }
    /// <summary>Authorizes and invokes exactly one registered name/version, enforcing JSON size and top-level shape.</summary>
    /// <param name="name">Case-sensitive namespaced activity identifier.</param>
    /// <param name="version">Positive registered wire-contract version.</param>
    /// <param name="context">Trusted runtime identity, never authored privileges.</param>
    /// <param name="input">Defined JSON whose source document must remain alive until the call completes.</param>
    /// <param name="cancellationToken">Cooperative cancellation token passed to authorization and application code.</param>
    /// <returns>A cloned JSON result independent of caller and activity document lifetimes.</returns>
    /// <exception cref="KeyNotFoundException">No activity matches the name and version.</exception>
    /// <exception cref="UnauthorizedAccessException">Current policy denies invocation.</exception>
    /// <exception cref="ArgumentException">Identity or JSON violates its contract.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested; external effects may still require reconciliation.</exception>
    ValueTask<JsonElement> InvokeAsync(string name, int version, ActivityInvocationContext context,
        JsonElement input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the current principal using a trusted runtime binding for an engine-generated instance ID.
/// Implementations must not derive privileges from authored workflow data. Missing/revoked bindings deny execution.
/// A durable deployment must persist its binding separately and re-evaluate current authorization on resume.
/// </summary>
public interface IWorkflowInvocationContextResolver
{
    /// <summary>Re-resolves a trusted actor binding and current principal for an invocation, including after resume.</summary>
    /// <param name="workflowInstanceId">Engine-generated instance ID, not an authored actor or role.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A context bound to this instance, or null for missing or revoked bindings; the bridge denies null and mismatches.</returns>
    /// <remarks>Resolved in the execution scope; singleton implementations must be thread-safe. The default stateless resolver always returns null.</remarks>
    ValueTask<ActivityInvocationContext?> ResolveAsync(string workflowInstanceId, CancellationToken cancellationToken);
}
