using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Sessions;

public sealed record ExistenceQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public SessionType? TypeFilter { get; init; }
    public int? Limit { get; init; }
}
