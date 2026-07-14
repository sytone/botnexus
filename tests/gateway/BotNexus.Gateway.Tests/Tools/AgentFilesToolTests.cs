using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class AgentFilesToolTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static string GetText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;

    private static IReadOnlyDictionary<string, object?> Args(string path)
        => new Dictionary<string, object?> { ["path"] = path };

    /// <summary>Path validator that allows every path, resolving to the raw string.</summary>
    private sealed class AllowAllValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    /// <summary>Path validator that denies every path.</summary>
    private sealed class DenyAllValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => false;
        public bool CanWrite(string absolutePath) => false;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => null;
    }

    // ── Definition ─────────────────────────────────────────────────────────

    [Fact]
    public void HasCorrectNameAndLabel()
    {
        var tool = new AgentFilesTool(new AllowAllValidator(), new MockFileSystem());
        tool.Name.ShouldBe("get_agent_files");
        tool.Label.ShouldBe("Get Agent Files");
        tool.Definition.Name.ShouldBe("get_agent_files");
    }

    [Fact]
    public async Task PrepareArguments_MissingPath_Throws()
    {
        var tool = new AgentFilesTool(new AllowAllValidator(), new MockFileSystem());
        await Should.ThrowAsync<ArgumentException>(() =>
            tool.PrepareArgumentsAsync(new Dictionary<string, object?>()));
    }

    // ── Access gating ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeniedPath_ReturnsAccessDenied()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("secret rules"));

        var tool = new AgentFilesTool(new DenyAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo/src"), CancellationToken.None);

        GetText(result).ShouldContain("Access denied");
    }

    // ── Discovery ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WalksFromPathToGitRoot_RootFirst()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("ROOT rules"));
        fs.AddFile("/repo/src/AGENTS.md", new MockFileData("SRC rules"));
        fs.AddDirectory("/repo/src/feature");

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo/src/feature"), CancellationToken.None);
        var text = GetText(result);

        text.ShouldContain("ROOT rules");
        text.ShouldContain("SRC rules");
        // Root-first: the repo-root file appears before the nested one.
        text.IndexOf("ROOT rules", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("SRC rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FilePathArgument_ReducedToContainingDirectory()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("root rules"));
        fs.AddFile("/repo/src/code.cs", new MockFileData("code"));

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo/src/code.cs"), CancellationToken.None);

        GetText(result).ShouldContain("root rules");
    }

    [Fact]
    public async Task StopsAtNearestGitRoot_DoesNotEscapeRepo()
    {
        var fs = new MockFileSystem();
        // Outer AGENTS.md above the repo boundary must NOT be included.
        fs.AddFile("/outer/AGENTS.md", new MockFileData("OUTER rules"));
        fs.AddDirectory("/outer/repo/.git");
        fs.AddFile("/outer/repo/AGENTS.md", new MockFileData("REPO rules"));
        fs.AddDirectory("/outer/repo/src");

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/outer/repo/src"), CancellationToken.None);
        var text = GetText(result);

        text.ShouldContain("REPO rules");
        text.ShouldNotContain("OUTER rules");
    }

    [Fact]
    public async Task NoGitRepo_ReturnsNoneMessage()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/loose/dir");
        fs.AddFile("/loose/AGENTS.md", new MockFileData("orphan"));

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/loose/dir"), CancellationToken.None);

        GetText(result).ShouldContain("No AGENTS.md files found");
    }

    [Fact]
    public async Task GitRepoWithoutAgentsMd_ReturnsNoneMessage()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddDirectory("/repo/src");

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo/src"), CancellationToken.None);

        GetText(result).ShouldContain("No AGENTS.md files found");
    }

    [Fact]
    public async Task EmptyAgentsMd_IsSkipped()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("   "));

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo"), CancellationToken.None);

        GetText(result).ShouldContain("No AGENTS.md files found");
    }

    [Fact]
    public async Task OversizedFile_IsTruncated()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        // ~40 KB, above the 16 KB per-file cap.
        fs.AddFile("/repo/AGENTS.md", new MockFileData(new string('x', 40 * 1024)));

        var tool = new AgentFilesTool(new AllowAllValidator(), fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo"), CancellationToken.None);

        GetText(result).ShouldContain("[truncated");
    }

    [Fact]
    public async Task NullValidator_SkipsAccessGating()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("open rules"));

        var tool = new AgentFilesTool(pathValidator: null, fileSystem: fs);
        var result = await tool.ExecuteAsync("call-1", Args("/repo"), CancellationToken.None);

        GetText(result).ShouldContain("open rules");
    }
}
