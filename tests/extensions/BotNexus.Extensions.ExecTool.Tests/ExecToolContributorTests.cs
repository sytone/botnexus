using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Wiring contract for <see cref="ExecToolContributor"/> (issue #2416).
/// <para>
/// The #2416 defect was not in <see cref="ExecTool"/>'s own path handling - it was that no code path
/// ever handed the tool a workspace, so the extension loader registered a workspace-less singleton.
/// These tests pin the replacement wiring: the tool is built per session against the session's
/// workspace, and the agent's tool allowlist is still honoured now that exec no longer flows through
/// the registry filter in the isolation strategy.
/// </para>
/// </summary>
public sealed class ExecToolContributorTests
{
    /// <summary>
    /// The contributed tool must run in the session workspace - the #2416 fix. Verified end to end by
    /// executing a child process and reading back its current directory.
    /// </summary>
    [Fact]
    public async Task Contribute_BuildsExecToolBoundToSessionWorkspace()
    {
        var workspace = CreateWorkspace();
        try
        {
            var contribution = await new ExecToolContributor(new MockFileSystem())
                .ContributeAsync(BuildContext(workspace));

            var tool = contribution.Tools.ShouldHaveSingleItem();
            tool.Name.ShouldBe("exec");

            string[] command = OperatingSystem.IsWindows()
                ? ["cmd.exe", "/c", "cd"]
                : ["/bin/pwd"];

            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = (IReadOnlyList<string>)command.ToList(),
            });
            var result = await tool.ExecuteAsync("t", args);

            Normalize(result.Content[0].Value).ShouldBe(Normalize(workspace));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// An allowlist that does not name <c>exec</c> must suppress the tool. Without this the move from
    /// the extension registry to a contributor would silently hand exec to agents configured without it.
    /// </summary>
    [Fact]
    public async Task Contribute_ReturnsNothing_WhenAllowlistExcludesExec()
    {
        var workspace = CreateWorkspace();
        try
        {
            var contribution = await new ExecToolContributor(new MockFileSystem())
                .ContributeAsync(BuildContext(workspace, toolIds: ["read", "write"]));

            contribution.Tools.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "*" }, true)]
    [InlineData(new[] { "exec" }, true)]
    [InlineData(new[] { "EXEC" }, true)]
    [InlineData(new[] { "read" }, false)]
    public void IsToolAllowed_MatchesIsolationStrategySemantics(string[] toolIds, bool expected)
    {
        ExecToolContributor.IsToolAllowed(toolIds, "exec").ShouldBe(expected);
    }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "botnexus-exec-contrib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AgentToolContributionContext BuildContext(string workspace, IReadOnlyList<string>? toolIds = null)
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("exec-test-agent"),
            DisplayName = "exec-test-agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ToolIds = toolIds ?? [],
        };

        return new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            workspace,
            new AllowAllPathValidator(),
            null,
            (_, _) => Task.FromResult<string?>(null));
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }
}
