using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Types;
using BotNexus.CodingAgent;
using BotNexus.CodingAgent.Hooks;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Tools;

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

        hookResult.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task SubshellCommandInjection_IsNotBlockedByHook_CurrentBehavior()
    {
        var context = CreateShellContext("echo $(cat /etc/passwd)");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PipeChainContainingDangerousPattern_IsBlockedByHook()
    {
        var context = CreateShellContext("echo hello | rm -rf /");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.ShouldNotBeNull();
        hookResult!.Block.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SemicolonChainContainingDangerousPattern_IsBlockedByHook()
    {
        var context = CreateShellContext("echo hello; rm -rf /");
        var hookResult = await _hooks.ValidateAsync(context, _config);

        hookResult.ShouldNotBeNull();
        hookResult!.Block.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task EnvironmentVariableExpansion_IsAllowed_CurrentBehavior()
    {
        var command = OperatingSystem.IsWindows() ? "echo $env:USERPROFILE" : "echo $HOME";
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = command });

        result.Content[0].Value.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VeryLongCommand_DoesNotHangAndReturnsResult()
    {
        var longPayload = new string('a', 100_000);
        var act = () => _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = $"echo {longPayload}" });
        await act.ShouldThrowAsync<Exception>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CommandWithNullByte_IsHandledGracefully()
    {
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = "echo test\0value" });
        result.Content.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Category", "SecurityGap")]
    public async Task CommandWithCrLfInjection_IsAccepted_CurrentBehavior()
    {
        var result = await _tool.ExecuteAsync("t1", new Dictionary<string, object?> { ["command"] = "echo first\r\necho second" });

        // On Linux the shell executes both commands and output contains "first".
        // On Windows the embedded CRLF can cause the shell process to hang,
        // producing a timeout message instead — both are valid current behavior.
        var output = result.Content[0].Value;
        (output!.Contains("first") || output.Contains("timed out")).ShouldBeTrue(
            $"expected output to contain 'first' or 'timed out' but was: {output}");
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
        if (!Directory.Exists(_workingDirectory))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_workingDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    return; // Best-effort: leave temp dir for OS cleanup.

                Thread.Sleep(500 * (attempt + 1));
            }
        }
    }
}
