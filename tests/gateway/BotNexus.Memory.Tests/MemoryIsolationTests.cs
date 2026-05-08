using BotNexus.Memory;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;

namespace BotNexus.Memory.Tests;

public sealed class MemoryIsolationTests
{
    [Fact]
    public async Task AgentA_CannotSearch_AgentB_Memories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "botnexus-memory-tests", Guid.NewGuid().ToString("N"));
        await using var factory = new MemoryStoreFactory(agentId => Path.Combine(tempRoot, agentId, "memory.db"));

        try
        {
            var storeA = factory.Create("agent-a");
            var storeB = factory.Create("agent-b");
            await storeA.InitializeAsync();
            await storeB.InitializeAsync();

            await storeA.InsertAsync(MemoryStoreTestContext.CreateEntry("a-1", "agent-a", "appleonlymemory"));
            await storeB.InsertAsync(MemoryStoreTestContext.CreateEntry("b-1", "agent-b", "bananaonlymemory"));

            var agentASearch = await storeA.SearchAsync("bananaonlymemory");
            var agentBSearch = await storeB.SearchAsync("bananaonlymemory");

            agentASearch.ShouldBeEmpty();
            agentBSearch.ShouldHaveSingleItem().Id.ShouldBe("b-1");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(tempRoot, true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(50);
                    }
                }
            }
        }
    }
}
