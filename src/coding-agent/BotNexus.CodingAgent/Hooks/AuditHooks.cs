using System.Collections.Concurrent;
using BotNexus.Agent.Core.Hooks;

namespace BotNexus.CodingAgent.Hooks;

public sealed class AuditHooks
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _startTimes = new(StringComparer.Ordinal);
    private int _toolCallCount;

    public AuditHooks(bool verbose = true)
    {
        Verbose = verbose;
    }

    public bool Verbose { get; }

    public int ToolCallCount => _toolCallCount;

    public void RegisterToolCallStart(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        _startTimes[toolCallId] = DateTimeOffset.UtcNow;
    }

    public Task<AfterToolCallResult?> AuditAsync(AfterToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Interlocked.Increment(ref _toolCallCount);
        var durationMs = ResolveDurationMs(context.ToolCallRequest.Id);

        if (Verbose)
        {
            var status = context.IsError ? "failed" : "succeeded";
            Console.WriteLine(
                $"[audit] tool={context.ToolCallRequest.Name} status={status} durationMs={durationMs} calls={count}");
        }

        return Task.FromResult<AfterToolCallResult?>(null);
    }

    private long ResolveDurationMs(string toolCallId)
    {
        if (_startTimes.TryRemove(toolCallId, out var startedAt))
        {
            return (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }

        return 0;
    }
}
