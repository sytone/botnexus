using System.Text;
using BotNexus.Agent.Providers.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Bounds how many bytes a single streamed tool call may accumulate into its argument buffer
/// (issue #2902). This is the per-tool-call complement to <see cref="ByteCountingStream"/>: that
/// wrapper bounds the raw SSE body, this one bounds the parsed <c>arguments</c> /
/// <c>partial_json</c> fragments that each provider stream parser appends into a
/// <see cref="StringBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream layer is the trust boundary against the provider. A malicious or malfunctioning
/// model/proxy can stream an unbounded number of argument fragments; before this guard each was
/// appended with no cumulative accounting, and the buffer was then doubled again by
/// <c>StringBuilder.ToString()</c> on every incremental parse. That is a remote-influenced
/// unbounded allocation in the hot path shared by every agent turn.
/// </para>
/// <para>
/// The budget is deliberately measured in <b>UTF-8 bytes appended</b> rather than
/// <see cref="StringBuilder.Length"/>. <c>Length</c> counts UTF-16 chars, so a payload of
/// multi-byte code points (or, worse, 4-byte astral characters that also cost two chars) would
/// let a hostile stream exceed the intended memory ceiling while the char counter still looked
/// healthy. Counting the encoded byte cost of each fragment as it arrives keeps the ceiling
/// honest for every payload shape.
/// </para>
/// <para>
/// On overflow the guard logs <b>exactly once per tool call</b> at warning level - subsequent
/// fragments for the same tool call are rejected silently, so a hostile stream cannot turn the
/// guard itself into a log-flood amplifier - and throws
/// <see cref="StreamToolArgumentsTooLargeException"/>. Throwing (rather than truncating) is the
/// deliberate choice required by the issue: a truncated argument buffer is invalid JSON, and
/// emitting it as if it were complete would hand the agent loop a silently-corrupted tool call.
/// Each provider's stream task already converts an escaping exception into a terminal
/// <c>ErrorEvent</c>/<see cref="Models.StopReason.Error"/>, so the failure is deterministic and
/// distinguishable at the caller.
/// </para>
/// <para>
/// Under-cap behaviour is byte-identical to before the guard: the fragment is appended verbatim
/// and nothing else about the parse changes.
/// </para>
/// </remarks>
public sealed class StreamToolArgumentBudget
{
    /// <summary>
    /// Default cumulative UTF-8 byte budget for one streamed tool call's arguments (1 MiB).
    /// </summary>
    /// <remarks>
    /// Chosen to sit far above any legitimate tool call - the largest real arguments are file
    /// writes and patches, which are orders of magnitude smaller - while still being a hard
    /// ceiling per call. It is deliberately <b>not</b> the 16 KiB
    /// <c>ToolInvocationRecord.DefaultMaxBytes</c>: that is a display/record-time sanitiser
    /// applied long after the buffer has already been grown, and reusing its value here would
    /// reject legitimate large tool calls that the gateway happily truncates for display today.
    /// The unit (UTF-8 bytes) is shared with that cap; only the magnitude differs.
    /// </remarks>
    public const long DefaultMaxBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Environment variable that overrides <see cref="DefaultMaxBytes"/> for every parser. Makes the
    /// budget configurable at deployment time without a code change; an absent, unparseable, or
    /// non-positive value falls back to the default rather than disabling the guard.
    /// </summary>
    public const string MaxBytesEnvironmentVariable = "BOTNEXUS_STREAM_TOOL_ARGUMENT_MAX_BYTES";

    private static long? _configuredMaxBytes;

    /// <summary>
    /// Gets the effective budget every provider stream parser uses: the process-wide override when
    /// one has been set by a composition root, otherwise the value of
    /// <see cref="MaxBytesEnvironmentVariable"/>, otherwise <see cref="DefaultMaxBytes"/>.
    /// </summary>
    public static long ConfiguredMaxBytes
    {
        get
        {
            if (_configuredMaxBytes is { } configured)
                return configured;

            var raw = Environment.GetEnvironmentVariable(MaxBytesEnvironmentVariable);
            if (long.TryParse(raw, out var parsed) && parsed > 0)
                return parsed;

            return DefaultMaxBytes;
        }

        // A null or non-positive assignment clears the override and restores the default; the guard
        // can be retuned but never switched off.
        set => _configuredMaxBytes = value > 0 ? value : null;
    }

    /// <summary>Clears any process-wide override, restoring environment/default resolution.</summary>
    public static void ResetConfiguredMaxBytes() => _configuredMaxBytes = null;

    /// <summary>
    /// Creates a budget for a single tool call using <see cref="ConfiguredMaxBytes"/>. This is the
    /// entry point every provider stream parser uses.
    /// </summary>
    public static StreamToolArgumentBudget ForToolCall(string provider, string modelId, string description) =>
        new(ConfiguredMaxBytes, provider, modelId, description);

    private readonly long _maxBytes;
    private readonly string _provider;
    private readonly string _modelId;
    private readonly string _description;
    private long _observedBytes;
    private bool _overflowLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamToolArgumentBudget"/> class for a single
    /// tool call.
    /// </summary>
    /// <param name="maxBytes">Cumulative UTF-8 byte budget. Must be positive.</param>
    /// <param name="provider">Provider name, reported in the overflow warning.</param>
    /// <param name="modelId">Model id, reported in the overflow warning.</param>
    /// <param name="description">
    /// Identifies which tool call overflowed (name or stream index) in the warning and the error.
    /// </param>
    public StreamToolArgumentBudget(long maxBytes, string provider, string modelId, string description)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be positive.");

        _maxBytes = maxBytes;
        _provider = provider;
        _modelId = modelId;
        _description = description;
    }

    /// <summary>Gets the cumulative UTF-8 byte budget this instance enforces.</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>
    /// Gets the cumulative UTF-8 byte cost of every fragment seen so far, including the fragment
    /// that tripped the budget. Reported in the warning and the exception.
    /// </summary>
    public long ObservedBytes => _observedBytes;

    /// <summary>
    /// Appends <paramref name="fragment"/> to <paramref name="target"/> if it fits within the
    /// remaining budget; otherwise logs once and throws.
    /// </summary>
    /// <param name="target">The tool call's argument accumulator.</param>
    /// <param name="fragment">The freshly-streamed argument fragment.</param>
    /// <exception cref="StreamToolArgumentsTooLargeException">
    /// The cumulative UTF-8 byte cost of the fragments seen for this tool call exceeds
    /// <see cref="MaxBytes"/>.
    /// </exception>
    public void Append(StringBuilder target, string fragment)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(fragment))
            return;

        // Measure the encoded cost of THIS fragment, not the accumulated char length: see the
        // UTF-8-vs-UTF-16 note in the type remarks.
        _observedBytes += Encoding.UTF8.GetByteCount(fragment);

        if (_observedBytes > _maxBytes)
        {
            if (!_overflowLogged)
            {
                _overflowLogged = true;
                ProviderDiagnostics
                    .CreateLogger(nameof(StreamToolArgumentBudget))
                    .LogWarning(
                        "Streamed tool-call arguments for {Description} exceeded the {MaxBytes}-byte budget "
                        + "(observed at least {ObservedBytes} bytes) from provider {Provider}, model {ModelId}. "
                        + "Accumulation was terminated and the tool call rejected.",
                        _description, _maxBytes, _observedBytes, _provider, _modelId);
            }

            throw new StreamToolArgumentsTooLargeException(
                _maxBytes, _observedBytes, _provider, _modelId, _description);
        }

        target.Append(fragment);
    }
}

/// <summary>
/// Thrown when one streamed tool call's accumulated arguments exceed the configured cumulative
/// UTF-8 byte budget (issue #2902). Distinct from
/// <see cref="ResponseContentTooLargeException"/>, which bounds the whole response body: this one
/// identifies the specific tool call whose argument accumulation was terminated, so the failure is
/// distinguishable at the caller rather than surfacing as a generic transport error.
/// </summary>
public sealed class StreamToolArgumentsTooLargeException : Exception
{
    /// <summary>Gets the byte budget that was exceeded.</summary>
    public long MaxBytes { get; }

    /// <summary>Gets the cumulative UTF-8 byte count at the point the budget was crossed.</summary>
    public long ObservedBytes { get; }

    /// <summary>Gets the provider whose stream produced the oversized tool call.</summary>
    public string Provider { get; }

    /// <summary>Gets the model whose stream produced the oversized tool call.</summary>
    public string ModelId { get; }

    /// <summary>Gets the tool call identity (name or stream index) that overflowed.</summary>
    public string Description { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamToolArgumentsTooLargeException"/> class.
    /// </summary>
    public StreamToolArgumentsTooLargeException(
        long maxBytes, long observedBytes, string provider, string modelId, string description)
        : base($"Streamed tool-call arguments for {description} exceeded the {maxBytes}-byte limit "
             + $"(observed at least {observedBytes} bytes) from provider {provider}, model {modelId}. "
             + "The tool call was rejected to prevent excessive memory use.")
    {
        MaxBytes = maxBytes;
        ObservedBytes = observedBytes;
        Provider = provider;
        ModelId = modelId;
        Description = description;
    }
}
