namespace BotNexus.Memory;

public sealed record MemorySearchFilter
{
    public string? SourceType { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? AfterDate { get; init; }
    public DateTimeOffset? BeforeDate { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}
