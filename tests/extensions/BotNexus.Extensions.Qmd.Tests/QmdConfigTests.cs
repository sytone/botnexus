using BotNexus.Extensions.Qmd;

namespace BotNexus.Extensions.Qmd.Tests;

public class QmdConfigTests
{
    [Fact]
    public void Default_config_has_expected_values()
    {
        var config = new QmdConfig();

        // QMD is opt-in: disabled by default (issue #2116).
        Assert.False(config.Enabled);
        Assert.Null(config.QmdPath);
        Assert.Equal("hybrid", config.DefaultSearchMode);
        Assert.Equal(10, config.MaxResults);
        Assert.Empty(config.Stores);
    }

    [Fact]
    public void Store_config_defaults_are_sensible()
    {
        var store = new QmdStoreConfig { Name = "test", Path = "/tmp/docs" };

        Assert.Equal("test", store.Name);
        Assert.Equal("/tmp/docs", store.Path);
        Assert.Null(store.Description);
        Assert.True(store.AutoUpdate);
        Assert.Equal(60, store.UpdateIntervalMinutes);
    }

    [Fact]
    public void Store_config_accepts_custom_values()
    {
        var store = new QmdStoreConfig
        {
            Name = "vault",
            Path = "/home/user/vault",
            Description = "Personal knowledge vault",
            AutoUpdate = false,
            UpdateIntervalMinutes = 120
        };

        Assert.Equal("vault", store.Name);
        Assert.Equal("/home/user/vault", store.Path);
        Assert.Equal("Personal knowledge vault", store.Description);
        Assert.False(store.AutoUpdate);
        Assert.Equal(120, store.UpdateIntervalMinutes);
    }
}
