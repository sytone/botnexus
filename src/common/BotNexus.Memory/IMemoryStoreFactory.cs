namespace BotNexus.Memory;

public interface IMemoryStoreFactory
{
    IMemoryStore Create(string agentId);
}
