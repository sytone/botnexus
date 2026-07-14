using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.SlashCommands;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for the opt-in per-command approval/protection hook added to the shared slash-command
/// core (issue #1950, part of #1580). Verifies that protected commands route through the injected
/// <see cref="ISlashCommandApprovalHook"/> (allow executes, deny blocks) and that unprotected
/// commands bypass the hook entirely.
/// </summary>
public class SlashCommandApprovalHookTests
{
    private const string AgentId = "agent-x";

    private static (SlashCommandDispatcher dispatcher, IAgentInteractionService interaction, ISlashCommandApprovalHook hook) CreateSut()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var hook = Substitute.For<ISlashCommandApprovalHook>();
        return (new SlashCommandDispatcher(interaction, hook), interaction, hook);
    }

    [Fact]
    public void SlashCommand_defaults_to_unprotected()
    {
        var cmd = new SlashCommand("/x", "desc", SlashCommandKind.SendToAgent);
        Assert.False(cmd.RequiresApproval);
    }

    [Fact]
    public void SlashCommand_supports_opt_in_protection_flag()
    {
        var cmd = new SlashCommand("/danger", "desc", SlashCommandKind.SendToAgent, RequiresApproval: true);
        Assert.True(cmd.RequiresApproval);
    }

    [Fact]
    public async Task Unprotected_command_bypasses_the_hook()
    {
        var (sut, interaction, hook) = CreateSut();
        var cmd = new SlashCommand("/help", "desc", SlashCommandKind.SendToAgent);

        var executed = await sut.ExecuteAsync(AgentId, cmd);

        Assert.True(executed);
        await hook.DidNotReceiveWithAnyArgs().IsApprovedAsync(default!, default!);
        await interaction.Received(1).SendMessageAsync(AgentId, "/help");
    }

    [Fact]
    public async Task Protected_command_consults_the_hook_and_executes_when_approved()
    {
        var (sut, interaction, hook) = CreateSut();
        var cmd = new SlashCommand("/danger", "desc", SlashCommandKind.SendToAgent, RequiresApproval: true);
        hook.IsApprovedAsync(AgentId, cmd).Returns(true);

        var executed = await sut.ExecuteAsync(AgentId, cmd);

        Assert.True(executed);
        await hook.Received(1).IsApprovedAsync(AgentId, cmd);
        await interaction.Received(1).SendMessageAsync(AgentId, "/danger");
    }

    [Fact]
    public async Task Protected_command_is_blocked_when_hook_denies()
    {
        var (sut, interaction, hook) = CreateSut();
        var cmd = new SlashCommand("/danger", "desc", SlashCommandKind.ResetSession, RequiresApproval: true);
        hook.IsApprovedAsync(AgentId, cmd).Returns(false);

        var executed = await sut.ExecuteAsync(AgentId, cmd);

        Assert.False(executed);
        await hook.Received(1).IsApprovedAsync(AgentId, cmd);
        await interaction.DidNotReceiveWithAnyArgs().ResetSessionAsync(default!);
        interaction.DidNotReceiveWithAnyArgs().ClearLocalMessages(default!);
        await interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Protected_command_fails_closed_when_no_hook_registered()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var sut = new SlashCommandDispatcher(interaction); // no hook
        var cmd = new SlashCommand("/danger", "desc", SlashCommandKind.SendToAgent, RequiresApproval: true);

        var executed = await sut.ExecuteAsync(AgentId, cmd);

        Assert.False(executed);
        await interaction.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!);
    }

    [Fact]
    public async Task Unprotected_command_still_executes_when_no_hook_registered()
    {
        var interaction = Substitute.For<IAgentInteractionService>();
        var sut = new SlashCommandDispatcher(interaction); // no hook
        var cmd = new SlashCommand("/compact", "desc", SlashCommandKind.CompactSession);

        var executed = await sut.ExecuteAsync(AgentId, cmd);

        Assert.True(executed);
        await interaction.Received(1).CompactSessionAsync(AgentId);
    }
}
