using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class BuiltInAgentRegistrationServiceTests
{
    private readonly DefaultAgentRegistry _registry = new(NullLogger<DefaultAgentRegistry>.Instance);
    private readonly BuiltInAgentRegistrationService _service;

    public BuiltInAgentRegistrationServiceTests()
    {
        _service = new BuiltInAgentRegistrationService(
            _registry,
            NullLogger<BuiltInAgentRegistrationService>.Instance);
    }

    [Fact]
    public async Task StartAsync_registers_all_builtin_agents()
    {
        await _service.StartAsync(CancellationToken.None);

        var registered = _registry.GetAll();
        Assert.Equal(BuiltInAgents.All.Count, registered.Count);
    }

    [Fact]
    public async Task StartAsync_agents_are_discoverable_by_id()
    {
        await _service.StartAsync(CancellationToken.None);

        foreach (var expected in BuiltInAgents.All)
        {
            var actual = _registry.Get(expected.AgentId);
            Assert.NotNull(actual);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
        }
    }

    [Fact]
    public async Task StartAsync_skips_already_registered_agents()
    {
        // Pre-register one agent
        _registry.Register(BuiltInAgents.Researcher);

        await _service.StartAsync(CancellationToken.None);

        // Should still have exactly 6 agents (not 7)
        var registered = _registry.GetAll();
        Assert.Equal(BuiltInAgents.All.Count, registered.Count);
    }

    [Fact]
    public async Task StartAsync_does_not_overwrite_preexisting_agent()
    {
        // Register a custom agent with the same ID
        var custom = new AgentDescriptor
        {
            AgentId = AgentId.From("researcher"),
            DisplayName = "Custom Researcher",
            ModelId = "custom-model",
            ApiProvider = "custom-provider",
        };
        _registry.Register(custom);

        await _service.StartAsync(CancellationToken.None);

        var actual = _registry.Get(AgentId.From("researcher"));
        Assert.NotNull(actual);
        Assert.Equal("Custom Researcher", actual.DisplayName);
    }

    [Fact]
    public async Task StopAsync_is_noop()
    {
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);

        // Agents remain registered after stop
        Assert.Equal(BuiltInAgents.All.Count, _registry.GetAll().Count);
    }

    [Fact]
    public async Task StartAsync_agents_have_correct_role_metadata()
    {
        await _service.StartAsync(CancellationToken.None);

        var coder = _registry.Get(AgentId.From("coder"));
        Assert.NotNull(coder);
        Assert.True(coder.Metadata.ContainsKey("role"));
        Assert.Equal("coder", coder.Metadata["role"]);
    }

    [Fact]
    public async Task StartAsync_agents_appear_as_code_based_in_config_service()
    {
        // This simulates the scenario where AgentConfigurationHostedService
        // captures code-based IDs after built-in registration
        await _service.StartAsync(CancellationToken.None);

        var codeBasedIds = _registry.GetAll()
            .Select(d => d.AgentId.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in BuiltInAgents.All)
        {
            Assert.Contains(agent.AgentId.Value, codeBasedIds);
        }
    }
}
