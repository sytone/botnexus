using System.Collections.Concurrent;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Accumulates, for the duration of a single agent turn, whether any tool result consumed on that
/// turn carried foreign content - and therefore whether a memory write originating from the turn
/// must be quarantined rather than recorded as first-party knowledge (issue #2519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an ambient scope rather than a parameter.</b> The taint is produced in
/// <c>ToolExecutor</c> and consumed in <c>MemorySaveTool</c>, which is an <see cref="IAgentTool"/>
/// like any other and is reached through the same uniform <c>ExecuteAsync</c> signature. Threading
/// a taint argument to the consumer would mean widening that signature for all ~55 tools so that
/// exactly one of them could read it, and every tool added afterwards would have to remember to
/// pass it through. An <see cref="AsyncLocal{T}"/> scope keeps the contract unchanged and, more
/// importantly, means a tool that knows nothing about taint cannot accidentally drop it.
/// </para>
/// <para>
/// <b>Monotonic by construction.</b> Taint only ever turns on. There is deliberately no method to
/// clear it within a scope: a turn that read a hostile web page does not become clean again because
/// a later local tool succeeded, and an API that permitted "untaint" would be the first thing an
/// injection payload tried to talk the model into calling. The only way back to a clean state is a
/// new scope, which is created per turn by the loop and disposed at its end.
/// </para>
/// <para>
/// <b>Flows across parallel tool execution.</b> <see cref="AsyncLocal{T}"/> propagates into the
/// tasks started for parallel tool dispatch, and the state object is shared by reference, so a
/// taint recorded by one concurrently executing tool is visible to the others and to the turn as a
/// whole. Mutation goes through a concurrent set for that reason.
/// </para>
/// </remarks>
public sealed class TurnTaintScope : IDisposable
{
    private static readonly AsyncLocal<TurnTaintState?> Current = new();

    private readonly TurnTaintState _state;
    private readonly TurnTaintState? _previous;
    private bool _disposed;

    private TurnTaintScope(TurnTaintState state, TurnTaintState? previous)
    {
        _state = state;
        _previous = previous;
    }

    /// <summary>
    /// Begins a new turn-scoped taint accumulation window, restoring the previous one on dispose.
    /// </summary>
    /// <remarks>
    /// Nesting restores rather than clears so a nested agent run (a sub-agent invoked as a tool)
    /// cannot leak its taint into the parent's window, nor erase the parent's on the way out.
    /// </remarks>
    public static TurnTaintScope Begin()
    {
        var previous = Current.Value;
        var state = new TurnTaintState();
        Current.Value = state;
        return new TurnTaintScope(state, previous);
    }

    /// <summary>The state for the active turn, or <see langword="null"/> when no scope is open.</summary>
    public static TurnTaintState? CurrentState => Current.Value;

    /// <summary>
    /// Records that a tool result from <paramref name="toolName"/> declaring
    /// <paramref name="contentSource"/> was consumed on the active turn. A no-op when no scope is
    /// open, and a no-op for a non-tainting source.
    /// </summary>
    public static void RecordToolResult(string toolName, string? contentSource)
        => Current.Value?.Record(toolName, contentSource);

    /// <summary>
    /// Whether the active turn is tainted. <see langword="false"/> when no scope is open, because
    /// "there is no turn in progress" is not the same claim as "an untrusted turn is in progress" -
    /// a memory write outside any agent turn (a cron rollup, an operator-driven API write) has no
    /// tool results to be tainted by, and marking it untrusted would flood the store with false
    /// quarantines and train operators to ignore the marker.
    /// </summary>
    public static bool IsCurrentTurnTainted => Current.Value?.IsTainted ?? false;

    /// <summary>The state this scope owns, for direct inspection by the code that opened it.</summary>
    public TurnTaintState State => _state;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Current.Value = _previous;
    }
}

/// <summary>
/// The mutable taint record for one turn: whether foreign content was consumed, and which tools
/// and sources contributed it.
/// </summary>
/// <remarks>
/// The contributing tool names and sources are retained, not just a boolean, because the
/// quarantine marker written into memory has to be actionable. "This note came from an untrusted
/// turn" is unfalsifiable and therefore unactionable; "this note came from a turn that read
/// <c>web_fetch</c> (network)" can be audited, and lets a reviewer decide whether the quarantine
/// was warranted.
/// </remarks>
public sealed class TurnTaintState
{
    private readonly ConcurrentDictionary<string, string> _contributors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any tainting tool result was consumed on this turn.</summary>
    public bool IsTainted => !_contributors.IsEmpty;

    /// <summary>
    /// The tools that tainted this turn, mapped to the normalised source they declared, ordered by
    /// tool name so the rendered marker is stable across runs and across parallel dispatch order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Contributors
        => _contributors.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Records a consumed tool result. Non-tainting sources are ignored; the value stored is always
    /// the normalised source, so an unrecognised declaration is recorded as
    /// <see cref="ToolContentSource.Unknown"/> rather than echoed verbatim into the marker.
    /// </summary>
    public void Record(string toolName, string? contentSource)
    {
        var normalized = ToolContentSource.Normalize(contentSource);
        if (!ToolContentSource.IsTainting(normalized))
            return;

        var name = string.IsNullOrWhiteSpace(toolName) ? "(unnamed tool)" : toolName.Trim();

        // A tool that returned both a network and an unknown result keeps the first recorded
        // classification. Both are tainting, so the distinction cannot change the outcome, and
        // last-write-wins would make the rendered marker depend on parallel completion order.
        _contributors.TryAdd(name, normalized);
    }

    /// <summary>
    /// Renders the human-readable origin summary embedded in a quarantined memory entry, e.g.
    /// <c>web_fetch (network), mcp_query (untrusted)</c>.
    /// </summary>
    public string DescribeContributors()
        => Contributors.Count == 0
            ? "no recorded contributors"
            : string.Join(", ", Contributors.Select(pair => $"{pair.Key} ({pair.Value})"));
}
