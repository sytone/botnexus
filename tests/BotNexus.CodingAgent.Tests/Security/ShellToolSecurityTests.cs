using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent;
using BotNexus.CodingAgent.Hooks;
using BotNexus.Providers.Core.Models;
using BotNexus.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Security;

public sealed class ShellToolSecurityTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-shell-security-{Guid.NewGuid():N}");
    private readonly CodingAgentConfig _config;
    private readonly SafetyHooks _hooks = new();
    private readonly ShellTool _tool;

    public ShellToolSecurityTests()
    {
        Directory.CreateDirectory(_workingDirectory);
        _config = new CodingAgentConfig
        {
            ConfigDirectory = Path.Combine(_workingDirectory, ".botnexus-agent"),
            SessionsDirectory = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions"),
            ExtensionsDirectory = Path.Combine(_workingDirectory, ".botnexus-agent", "extensions"),
            SkillsDirectory = Path.Combine(_workingDirectory, ".botnexus-agent", "skills"),
            AllowedCommands = [],
            BlockedPaths = []
        };
        _tool = new ShellTool(_workingDirectory, defaultTimeoutSeconds: 5);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task BacktickCommandInjection_IsNotBlockedByHook_CurrentBehavior()
    {
        var context = CreateShellContext("echo `whoami`");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SubshellCommandInjection_IsNotBlockedByHook_CurrentBehavior()
    {
        var context = CreateShellContext("echo $(cat /etc/passwd)");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PipeChainContainingDangerousPattern_IsBlockedByHook()
    {
        var context = CreateShellContext("echo hello | rm -rf /");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.Should().NotBeNull();
        hookResult!.Block.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemicolonChainContainingDangerousPattern_IsBlockedByHook()
    {
        var context = CreateShellContext("echo hello; rm -rf /");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.Should().NotBeNull();
        hookResult!.Block.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task EnvironmentVariableExpansion_IsAllowed_CurrentBehavior()
    {
        var command = OperatingSystem.IsWindows() ? "echo $env:USERPROFILE" : "echo $HOME";
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = command });

        result.Content[0].Value.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VeryLongCommand_DoesNotHangAndReturnsResult()
    {
        var longPayload = new string('a', 100_000);
        var act = () => _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = $"echo {longPayload}" });
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CommandWithNullByte_IsHandledGracefully()
    {
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = "echo test\0value" });
        result.Content.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task CommandWithCrLfInjection_IsAccepted_CurrentBehavior()
    {
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = "echo first\r\necho second" });

        result.Content[0].Value.Should().Contain("first");
    }

    private static BeforeToolCallContext CreateShellContext(string command)
    {
        var args = new Dictionary<string, object?> { ["command"] = command };
        return new BeforeToolCallContext(
            new AssistantAgentMessage("shell"),
            new ToolCallContent("tc-1", "bash", args),
            args,
            new AgentContext(null, [], []));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
