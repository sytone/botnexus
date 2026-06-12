namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Mutable accumulator for run metrics during an agent loop execution.
/// Thread-safe is not required — only accessed from the single-threaded loop.
/// </summary>
internal sealed class RunMetricsAccumulator
{
    private readonly DateTimeOffset _startTime;
    private long _inputTokens;
    private long _outputTokens;
    private int _turnCount;
    private int _toolCallCount;

    public RunMetricsAccumulator(DateTimeOffset startTime)
    {
        _startTime = startTime;
    }

    public void AddTokens(int? inputTokens, int? outputTokens)
    {
        _inputTokens += inputTokens ?? 0;
        _outputTokens += outputTokens ?? 0;
    }

    public void IncrementTurns() => _turnCount++;

    public void AddToolCalls(int count) => _toolCallCount += count;

    public Types.RunMetrics ToMetrics(DateTimeOffset endTime) => new(
        _inputTokens,
        _outputTokens,
        _turnCount,
        _toolCallCount,
        endTime - _startTime);
}
