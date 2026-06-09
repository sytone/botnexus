using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests;

public sealed class DockerSandboxOptionsResolverTests
{
    private static readonly DockerSandboxOptions Defaults = new()
    {
        Image = "default-image:1.0",
        NetworkEnabled = false,
        MemoryLimit = "512m",
        IdleTimeout = TimeSpan.FromMinutes(10)
    };

    private static AgentDescriptor MakeDescriptor(
        IReadOnlyDictionary<string, object?>? isolationOptions = null) => new()
    {
        AgentId = AgentId.From("test-agent"),
        DisplayName = "Test",
        ModelId = "model",
        ApiProvider = "provider",
        IsolationStrategy = "docker-sandbox",
        IsolationOptions = isolationOptions ?? new Dictionary<string, object?>()
    };

    [Fact]
    public void Resolve_NoOverrides_ReturnsGlobalDefaults()
    {
        var descriptor = MakeDescriptor();
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.Image.ShouldBe("default-image:1.0");
        result.NetworkEnabled.ShouldBeFalse();
        result.MemoryLimit.ShouldBe("512m");
        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Resolve_StringOverrides_Applied()
    {
        var options = new Dictionary<string, object?>
        {
            ["image"] = "custom:2.0",
            ["memoryLimit"] = "1g"
        };
        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.Image.ShouldBe("custom:2.0");
        result.MemoryLimit.ShouldBe("1g");
        // Unspecified fields remain defaults
        result.NetworkEnabled.ShouldBeFalse();
        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Resolve_BoolOverride_Applied()
    {
        var options = new Dictionary<string, object?>
        {
            ["networkEnabled"] = true
        };
        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.NetworkEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_TimeSpanOverride_Applied()
    {
        var options = new Dictionary<string, object?>
        {
            ["idleTimeout"] = "00:05:00"
        };
        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Resolve_JsonElementValues_ParsedCorrectly()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            image = "json-img:latest",
            networkEnabled = true,
            memoryLimit = "2g",
            idleTimeout = "00:15:00"
        });

        var options = new Dictionary<string, object?>();
        foreach (var prop in json.EnumerateObject())
        {
            options[prop.Name] = prop.Value;
        }

        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.Image.ShouldBe("json-img:latest");
        result.NetworkEnabled.ShouldBeTrue();
        result.MemoryLimit.ShouldBe("2g");
        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Resolve_NullValues_FallBackToDefaults()
    {
        var options = new Dictionary<string, object?>
        {
            ["image"] = null,
            ["networkEnabled"] = null,
            ["memoryLimit"] = null,
            ["idleTimeout"] = null
        };
        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.Image.ShouldBe("default-image:1.0");
        result.NetworkEnabled.ShouldBeFalse();
        result.MemoryLimit.ShouldBe("512m");
        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Resolve_InvalidTimeSpan_FallsBackToDefault()
    {
        var options = new Dictionary<string, object?>
        {
            ["idleTimeout"] = "not-a-timespan"
        };
        var descriptor = MakeDescriptor(options);
        var result = DockerSandboxOptionsResolver.Resolve(Defaults, descriptor);

        result.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Resolve_NullDefaults_Throws()
    {
        var descriptor = MakeDescriptor();
        Should.Throw<ArgumentNullException>(() => DockerSandboxOptionsResolver.Resolve(null!, descriptor));
    }

    [Fact]
    public void Resolve_NullDescriptor_Throws()
    {
        Should.Throw<ArgumentNullException>(() => DockerSandboxOptionsResolver.Resolve(Defaults, null!));
    }

    [Fact]
    public void Resolve_GlobalMemoryLimitNull_ReturnsNull()
    {
        var defaults = new DockerSandboxOptions
        {
            Image = "img",
            NetworkEnabled = false,
            MemoryLimit = null,
            IdleTimeout = TimeSpan.FromMinutes(5)
        };
        var descriptor = MakeDescriptor();
        var result = DockerSandboxOptionsResolver.Resolve(defaults, descriptor);

        result.MemoryLimit.ShouldBeNull();
    }
}
