using BotNexus.Gateway.Configuration;
using Xunit;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class TitlingConfigMigrationTests
{
    [Fact]
    public void Load_WithStringTitling_MigratesToObject()
    {
        var json = """
        {
            "version": 1,
            "gateway": {
                "auxiliary": {
                    "titling": "gpt-4o-mini"
                }
            }
        }
        """;

        var config = LoadFromJson(json);

        Assert.NotNull(config.Gateway?.Auxiliary?.Titling);
        Assert.Equal("gpt-4o-mini", config.Gateway!.Auxiliary!.Titling!.Model);
        Assert.Equal(30, config.Gateway.Auxiliary.Titling.TimeoutSeconds);
    }

    [Fact]
    public void Load_WithObjectTitling_PreservesConfig()
    {
        var json = """
        {
            "version": 1,
            "gateway": {
                "auxiliary": {
                    "titling": { "model": "claude-haiku-3-5", "timeoutSeconds": 15 }
                }
            }
        }
        """;

        var config = LoadFromJson(json);

        Assert.NotNull(config.Gateway?.Auxiliary?.Titling);
        Assert.Equal("claude-haiku-3-5", config.Gateway!.Auxiliary!.Titling!.Model);
        Assert.Equal(15, config.Gateway.Auxiliary.Titling.TimeoutSeconds);
    }

    [Fact]
    public void Load_WithNullTitling_ReturnsNull()
    {
        var json = """
        {
            "version": 1,
            "gateway": {
                "auxiliary": {
                    "titling": null
                }
            }
        }
        """;

        var config = LoadFromJson(json);

        Assert.Null(config.Gateway?.Auxiliary?.Titling);
    }

    [Fact]
    public void Load_WithEmptyStringTitling_ReturnsNull()
    {
        var json = """
        {
            "version": 1,
            "gateway": {
                "auxiliary": {
                    "titling": ""
                }
            }
        }
        """;

        var config = LoadFromJson(json);

        Assert.Null(config.Gateway?.Auxiliary?.Titling);
    }

    [Fact]
    public void Load_WithNoTitling_ReturnsNullTitling()
    {
        var json = """
        {
            "version": 1,
            "gateway": {
                "auxiliary": {}
            }
        }
        """;

        var config = LoadFromJson(json);

        Assert.Null(config.Gateway?.Auxiliary?.Titling);
    }

    private static PlatformConfig LoadFromJson(string json)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);
            return PlatformConfigLoader.Load(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}