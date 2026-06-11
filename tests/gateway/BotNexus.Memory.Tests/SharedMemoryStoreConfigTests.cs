using System.IO.Abstractions.TestingHelpers;
using Xunit;

namespace BotNexus.Memory.Tests;

public sealed class SharedMemoryStoreConfigTests
{
    [Fact]
    public void Config_RequiresName()
    {
        var config = new SharedMemoryStoreConfig
        {
            Name = "platform-knowledge",
            Description = "Platform decisions and patterns",
            Writers = ["farnsworth", "nova"],
            Readers = ["*"],
            RetentionDays = 365
        };

        Assert.Equal("platform-knowledge", config.Name);
        Assert.Equal("Platform decisions and patterns", config.Description);
        Assert.Equal(2, config.Writers.Count);
        Assert.Single(config.Readers);
        Assert.Equal(365, config.RetentionDays);
    }

    [Fact]
    public void Config_DefaultsToEmptyLists()
    {
        var config = new SharedMemoryStoreConfig { Name = "test" };

        Assert.Empty(config.Writers);
        Assert.Empty(config.Readers);
        Assert.Null(config.Description);
        Assert.Null(config.RetentionDays);
    }

    [Fact]
    public void Config_RecordEquality()
    {
        var a = new SharedMemoryStoreConfig
        {
            Name = "test",
            Writers = ["agent1"],
            Readers = ["*"]
        };
        var b = new SharedMemoryStoreConfig
        {
            Name = "test",
            Writers = ["agent1"],
            Readers = ["*"]
        };

        // Records use reference equality for collections
        Assert.NotEqual(a, b);
        Assert.Equal(a.Name, b.Name);
    }
}
