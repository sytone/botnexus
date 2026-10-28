using System;
using System.IO;
using System.Text.Json;

namespace BotNexus.TestEventCollector;

/// <summary>Flushes each observed test event independently so an outer kill cannot erase in-flight identity.</summary>
public sealed class EventJournal : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object sync = new();
    private long sequence;

    /// <summary>Creates a private-run journal with a unique path chosen by the collector.</summary>
    public EventJournal(string path) => writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    /// <summary>Persists the actual collector event, not an inference from a TRX or diagnostic marker.</summary>
    public void Record(string eventName, Guid id, string fullyQualifiedName, string displayName, string source)
    {
        lock (sync)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                sequence = ++sequence,
                utc = DateTime.UtcNow.ToString("o"),
                @event = eventName,
                id,
                fullyQualifiedName,
                displayName,
                source,
                processId = Environment.ProcessId
            }));
        }
    }

    /// <summary>Persists the actual launched testhost PID and start identity for the absolute watchdog.</summary>
    public void RecordHost(int testHostProcessId, DateTime testHostStartUtc)
    {
        lock (sync)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                sequence = ++sequence,
                utc = DateTime.UtcNow.ToString("o"),
                @event = "TestHostLaunched",
                processId = Environment.ProcessId,
                testHostProcessId,
                testHostStartUtc = testHostStartUtc.ToString("o"),
                testHostStartToken = OperatingSystem.IsLinux()
                    ? ReadLinuxStartToken(testHostProcessId)
                    : testHostStartUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        }
    }

    private static string ReadLinuxStartToken(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        // The command name can contain spaces and parentheses. Field 22 is starttime,
        // indexed from field 3 after the final closing parenthesis.
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }

    /// <summary>Closes the journal after the collection session ends.</summary>
    public void Dispose() { lock (sync) writer.Dispose(); }
}
