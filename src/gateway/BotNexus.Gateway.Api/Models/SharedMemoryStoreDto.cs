namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// A shared memory store as the portal shows it: who it is for, and who can reach it.
/// </summary>
/// <remarks>
/// Deliberately carries no entries. The contents of a shared store are reachable through the
/// existing per-store search; what is NOT visible anywhere is the access relationship, which
/// until now lived only in config.json. Several agents writing to one store is the arrangement
/// worth being able to see at a glance, because it is the one where a note written by one agent
/// becomes an instruction another agent reads.
/// </remarks>
public sealed record SharedMemoryStoreDto
{
    /// <summary>The store's unique name.</summary>
    public required string Name { get; init; }

    /// <summary>What the store is for, as the operator described it.</summary>
    public string? Description { get; init; }

    /// <summary>Configured reader access list, verbatim — may contain "*".</summary>
    public IReadOnlyList<string> Readers { get; init; } = [];

    /// <summary>Configured writer access list, verbatim — may contain "*".</summary>
    public IReadOnlyList<string> Writers { get; init; } = [];

    /// <summary>Days entries are kept, or null for indefinitely.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>
    /// How many agents on the CURRENT roster can read this store, with "*" already resolved.
    /// </summary>
    public int ReaderCount { get; init; }

    /// <summary>
    /// How many agents on the current roster can write to it. The number that matters: a store
    /// every agent can write is a very different object from one a single curator maintains.
    /// </summary>
    public int WriterCount { get; init; }
}
