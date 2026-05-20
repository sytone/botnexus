using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class AgentWorkspaceScaffolderTests
{
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "botnexus-scaffold-tests");

    [Fact]
    public async Task StartAsync_EnsuresScaffoldForAllRegisteredAgents()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("nova"));
        registry.Register(CreateDescriptor("farnsworth"));
        var scaffolder = new AgentWorkspaceScaffolder(registry, home, NullLogger<AgentWorkspaceScaffolder>.Instance);

        await scaffolder.StartAsync(CancellationToken.None);

        foreach (var agentId in new[] { "nova", "farnsworth" })
        {
            var workspacePath = Path.Combine(HomePath, "agents", agentId, "workspace");
            fs.Directory.Exists(workspacePath).ShouldBeTrue($"{agentId} workspace should exist");
            fs.File.Exists(Path.Combine(workspacePath, "SOUL.md")).ShouldBeTrue($"{agentId} SOUL.md should be scaffolded");
            fs.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeTrue($"{agentId} BOOTSTRAP.md should be scaffolded");
            fs.File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBeTrue($"{agentId} IDENTITY.md should be scaffolded");
        }
    }

    [Fact]
    public async Task StartAsync_WhenWorkspaceAlreadyExists_EnsuresMissingFilesWithoutOverwriting()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);
        // Pre-create workspace with only SOUL.md - simulates existing agent missing BOOTSTRAP.md
        var workspacePath = Path.Combine(HomePath, "agents", "nova", "workspace");
        fs.Directory.CreateDirectory(workspacePath);
        fs.File.WriteAllText(Path.Combine(workspacePath, "SOUL.md"), "custom soul content");

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("nova"));
        var scaffolder = new AgentWorkspaceScaffolder(registry, home, NullLogger<AgentWorkspaceScaffolder>.Instance);

        await scaffolder.StartAsync(CancellationToken.None);

        // Existing file must not be overwritten
        fs.File.ReadAllText(Path.Combine(workspacePath, "SOUL.md")).ShouldBe("custom soul content");
        // Missing files must be created
        fs.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "IDENTITY.md")).ShouldBeTrue();
        fs.File.Exists(Path.Combine(workspacePath, "USER.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_SkipsSubAgentWorkspaces()
    {
        var fs = new MockFileSystem();
        var home = new BotNexusHome(fs, HomePath);
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(CreateDescriptor("nova--subagent--general--abc123"));
        var scaffolder = new AgentWorkspaceScaffolder(registry, home, NullLogger<AgentWorkspaceScaffolder>.Instance);

        await scaffolder.StartAsync(CancellationToken.None);

        // Sub-agent should not be scaffolded in the persistent agents directory
        fs.Directory.Exists(Path.Combine(HomePath, "agents", "nova--subagent--general--abc123")).ShouldBeFalse();
    }

    private static AgentDescriptor CreateDescriptor(string agentId) =>
        new() { AgentId = AgentId.From(agentId), DisplayName = agentId, ModelId = "model", ApiProvider = "provider" };
}
