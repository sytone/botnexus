using System.Collections.Concurrent;

namespace BotNexus.Extensions.Channels.Test;

/// <summary>
/// A structured log entry captured by the test channel's in-memory sink.
/// </summary>
/// <param name="TimestampUtc">When the entry was written.</param>
/// <param name="Level">Log level name (<c>Information</c>, <c>Warning</c>, …).</param>
/// <param name="Category">The logger category the entry was written to.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The formatted exception, when one was attached.</param>
/// <param name="Properties">
/// Structured state, flattened to strings. Present so a test can assert on a named property
/// (<c>botnexus.channel.type</c>) rather than pattern-matching the rendered text, which changes
/// every time someone rewords a log message.
/// </param>
public sealed record TestChannelLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string?> Properties);

/// <summary>
/// Bounded in-memory buffer of captured log entries, shared by the logger provider that writes to
/// it and the HTTP endpoints that read it.
/// </summary>
/// <remarks>
/// <para>
/// The buffer is a ring: once <see cref="TestChannelOptions.MaxCapturedLogEntries"/> is reached the
/// OLDEST entries are evicted. A gateway under test emits log entries continuously, so an unbounded
/// buffer would be a memory leak with a plausible-looking justification.
/// </para>
/// <para>
/// Eviction is deliberately visible: <see cref="DroppedEntryCount"/> reports how many entries were
/// discarded, so a test that finds nothing can distinguish "it was never logged" from "it scrolled
/// out of the window". Reporting a partial view as if it were complete is the failure mode this
/// counter exists to prevent.
/// </para>
/// </remarks>
public sealed class TestChannelLogCapture
{
    private readonly ConcurrentQueue<TestChannelLogEntry> _entries = new();
    private readonly int _capacity;
    private long _dropped;

    /// <summary>Creates a capture buffer bounded to <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">Maximum retained entries; values below 1 are clamped to 1.</param>
    public TestChannelLogCapture(int capacity = 2000) => _capacity = Math.Max(1, capacity);

    /// <summary>Maximum number of entries retained before the oldest are evicted.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Number of entries evicted because the buffer was full. Non-zero means the returned view is
    /// INCOMPLETE and a negative assertion ("this was never logged") is not supportable from it.
    /// </summary>
    public long DroppedEntryCount => Interlocked.Read(ref _dropped);

    /// <summary>Appends an entry, evicting the oldest when the buffer is full.</summary>
    public void Add(TestChannelLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Returns a snapshot of the retained entries in capture order.</summary>
    public IReadOnlyList<TestChannelLogEntry> Snapshot() => [.. _entries];

    /// <summary>Clears the buffer and the dropped-entry counter.</summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            // Drain.
        }

        Interlocked.Exchange(ref _dropped, 0);
    }
}
