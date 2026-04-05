using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent;
using BotNexus.CodingAgent.Hooks;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Hooks;

public sealed class SafetyHooksTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-safety-{Guid.NewGuid():N}");
    private readonly SafetyHooks _hooks = new();
    private readonly CodingAgentConfig _config;

    public SafetyHooksTests()
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
    }

    [Fact]
    public async Task ValidateAsync_BlocksPathTraversal()
    {
        var context = CreateContext("write", new Dictionary<string, object?>
        {
            ["path"] = "..\\outside.txt",
            ["content"] = "data"
        });

        var result = await _hooks.ValidateAsync(context, _config);

        result.Should().NotBeNull();
        result!.Block.Should().BeTrue();
        result.Reason.Should().Contain("Unsafe path");
    }

    [Fact]
    public async Task ValidateAsync_BlocksDangerousCommand()
    {
        var context = CreateContext("bash", new Dictionary<string, object?>
        {
            ["command"] = "rm -rf /"
        });

        var result = await _hooks.ValidateAsync(context, _config);

        result.Should().NotBeNull();
        result!.Block.Should().BeTrue();
        result.Reason.Should().Contain("dangerous command pattern");
    }

    [Fact]
    public async Task ValidateAsync_AllowsNormalOperations()
    {
        var writeContext = CreateContext("write", new Dictionary<string, object?>
        {
            ["path"] = "safe.txt",
            ["content"] = "ok"
        });
        var shellContext = CreateContext("bash", new Dictionary<string, object?>
        {
            ["command"] = "echo hello"
        });

        var writeResult = await _hooks.ValidateAsync(writeContext, _config);
        var shellResult = await _hooks.ValidateAsync(shellContext, _config);

        writeResult.Should().BeNull();
        shellResult.Should().BeNull();
    }

    private static BeforeToolCallContext CreateContext(string toolName, Dictionary<string, object?> args)
    {
        var assistant = new AssistantAgentMessage("run tool");
        var toolCall = new ToolCallContent("tool-1", toolName, args);
        return new BeforeToolCallContext(assistant, toolCall, args, new AgentContext(null, [], []));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
