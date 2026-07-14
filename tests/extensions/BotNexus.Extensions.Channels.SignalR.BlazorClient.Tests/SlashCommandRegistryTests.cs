using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the shared slash-command registry and dispatcher lifted into BlazorClient.Core
/// (issue #1949, part of #1580). Guards the full command surface and the command-to-interaction
/// mapping that both the desktop and mobile chat palettes now consume.
/// </summary>
public class SlashCommandRegistryTests
{
    [Fact]
    public void Registry_exposes_full_command_surface_not_just_quick_actions()
    {
        var names = SlashCommandRegistry.All.Select(c => c.Name).ToArray();

        // Original desktop quick actions preserved.
        Assert.Contains("/new", names);
        Assert.Contains("/compact", names);
        Assert.Contains("/clear", names);
        Assert.Contains("/prompts", names);

        // Full gateway command surface (Jon 2026-06-24: all commands supported).
        Assert.Contains("/help", names);
        Assert.Contains("/status", names);
        Assert.Contains("/agents", names);
        Assert.Contains("/context", names);
        Assert.Contains("/model", names);
        Assert.Contains("/reasoning", names);
    }

    [Fact]
    public void Registry_command_names_are_unique_and_start_with_slash()
    {
        Assert.All(SlashCommandRegistry.All, c => Assert.StartsWith("/", c.Name));
        Assert.All(SlashCommandRegistry.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        var distinct = SlashCommandRegistry.All.Select(c => c.Name).Distinct().Count();
        Assert.Equal(SlashCommandRegistry.All.Count, distinct);
    }

    [Fact]
    public void Filter_returns_all_for_bare_slash()
    {
        var result = SlashCommandRegistry.Filter("/");
        Assert.Equal(SlashCommandRegistry.All.Count, result.Count);
    }

    [Fact]
    public void Filter_matches_prefix_case_insensitively()
    {
        var result = SlashCommandRegistry.Filter("/CO");
        var names = result.Select(c => c.Name).ToArray();
        Assert.Contains("/compact", names);
        Assert.Contains("/context", names);
        Assert.DoesNotContain("/new", names);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("/new extra")]
    public void Filter_returns_empty_for_non_command_input(string input)
    {
        Assert.Empty(SlashCommandRegistry.Filter(input));
    }
}

/// <summary>
/// Tests that the dispatcher maps each command kind to the correct
/// <see cref="IAgentInteractionService"/> call, preserving desktop behaviour.
/// </summary>
public class SlashCommandDispatcherTests
{
    private const string AgentId = "agent-x";

    private static (SlashCommandDispatcher dispatcher, IAgentInteractionService interaction) CreateSut()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        return (new SlashCommandDispatcher(interaction), interaction);
    }

    [Fact]
    public async Task New_resets_session()
    {
        var (sut, interaction) = CreateSut();
        var cmd = SlashCommandRegistry.All.First(c => c.Name == "/new");

        await sut.ExecuteAsync(AgentId, cmd);

        await interaction.Received(1).ResetSessionAsync(AgentId);
    }

    [Fact]
    public async Task Compact_compacts_session()
    {
        var (sut, interaction) = CreateSut();
        var cmd = SlashCommandRegistry.All.First(c => c.Name == "/compact");

        await sut.ExecuteAsync(AgentId, cmd);

        await interaction.Received(1).CompactSessionAsync(AgentId);
    }

    [Fact]
    public async Task Clear_clears_local_messages()
    {
        var (sut, interaction) = CreateSut();
        var cmd = SlashCommandRegistry.All.First(c => c.Name == "/clear");

        await sut.ExecuteAsync(AgentId, cmd);

        interaction.Received(1).ClearLocalMessages(AgentId);
    }

    [Fact]
    public async Task Prompts_sends_command_text_to_agent()
    {
        var (sut, interaction) = CreateSut();
        var cmd = SlashCommandRegistry.All.First(c => c.Name == "/prompts");

        await sut.ExecuteAsync(AgentId, cmd);

        await interaction.Received(1).SendMessageAsync(AgentId, "/prompts");
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/status")]
    [InlineData("/agents")]
    [InlineData("/context")]
    [InlineData("/model")]
    [InlineData("/reasoning")]
    public async Task Gateway_commands_are_sent_to_agent(string name)
    {
        var (sut, interaction) = CreateSut();
        var cmd = SlashCommandRegistry.All.First(c => c.Name == name);

        await sut.ExecuteAsync(AgentId, cmd);

        await interaction.Received(1).SendMessageAsync(AgentId, name);
    }

    [Fact]
    public async Task Every_registry_command_dispatches_to_exactly_one_interaction_call()
    {
        foreach (var cmd in SlashCommandRegistry.All)
        {
            var (sut, interaction) = CreateSut();

            await sut.ExecuteAsync(AgentId, cmd);

            var totalCalls = interaction.ReceivedCalls().Count();
            Assert.True(totalCalls == 1, $"Command {cmd.Name} made {totalCalls} interaction calls, expected exactly 1.");
        }
    }
}
