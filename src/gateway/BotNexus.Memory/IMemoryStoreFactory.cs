using BotNexus.Domain.Primitives;

namespace BotNexus.Memory;

public interface IMemoryStoreFactory
{
    IMemoryStore Create(AgentId agentId);

    /// <summary>
    /// Indicates whether the location backing this agent's memory store still exists.
    /// A sub-agent whose workspace has been reaped by the sweeper (#2237) has no directory,
    /// and therefore no <c>data/memory.sqlite</c>; opening it would fail with
    /// <c>SQLITE_CANTOPEN</c>, which is permanently unrecoverable rather than transient (#2608).
    /// Implementations that are not filesystem-backed report <see langword="true"/>.
    /// </summary>
    bool StoreLocationExists(AgentId agentId) => true;
}
