using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Thread-safe in-memory ring buffer that captures Warning+ log entries
/// and deduplicates them by message template fingerprint.
/// </summary>
public sealed class LogDiagnosticsRingBuffer
{
    private readonly ConcurrentDictionary<string, LogPatternEntry> _patterns = new();
    private readonly int _maxPatterns;

    /// <summary>
    /// Creates a new ring buffer with a maximum pattern capacity.
    /// </summary>
    /// <param name="maxPatterns">Maximum number of unique patterns to retain (default 1000).</param>
    public LogDiagnosticsRingBuffer(int maxPatterns = 1000)
    {
        _maxPatterns = maxPatterns;
    }

    /// <summary>
    /// Records a log entry. If the template fingerprint already exists, increments count and updates LastSeen.
    /// </summary>
    public void Record(LogLevel level, string? messageTemplate, string renderedMessage)
    {
        if (level < LogLevel.Warning)
            return;

        var template = messageTemplate ?? renderedMessage;
        var fingerprint = ComputeFingerprint(template, level);

        _patterns.AddOrUpdate(
            fingerprint,
            _ => new LogPatternEntry
            {
                Fingerprint = fingerprint,
                Template = template,
                Severity = level,
                Count = 1,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
                SampleMessage = Truncate(renderedMessage, 500)
            },
            (_, existing) =>
            {
                existing.Count++;
                existing.LastSeen = DateTimeOffset.UtcNow;
                return existing;
            });

        // Evict oldest if over capacity
        if (_patterns.Count > _maxPatterns)
        {
            var oldest = _patterns.Values
                .OrderBy(p => p.LastSeen)
                .FirstOrDefault();

            if (oldest is not null)
                _patterns.TryRemove(oldest.Fingerprint, out _);
        }
    }

    /// <summary>
    /// Returns all patterns observed within the specified time window, sorted by LastSeen descending.
    /// </summary>
    public IReadOnlyList<LogPatternEntry> GetPatterns(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        return _patterns.Values
            .Where(p => p.LastSeen >= cutoff)
            .OrderByDescending(p => p.LastSeen)
            .ToList();
    }

    /// <summary>
    /// Returns the total number of unique patterns currently in the buffer.
    /// </summary>
    public int PatternCount => _patterns.Count;

    /// <summary>
    /// Clears all patterns from the buffer.
    /// </summary>
    public void Clear() => _patterns.Clear();

    internal static string ComputeFingerprint(string template, LogLevel level)
    {
        var input = $"{level}:{template}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..16];
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..maxLength] + "...";
    }
}
