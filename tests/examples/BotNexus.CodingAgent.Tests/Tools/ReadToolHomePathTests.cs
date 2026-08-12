using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Paths;
using BotNexus.Tools;
using Shouldly;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Pins <see cref="ReadTool"/>'s handling of an agent-supplied <c>~/&lt;file&gt;</c> path (issue #3013, AC3).
/// </summary>
/// <remarks>
/// Before this change <c>ReadTool</c> had no <c>~</c> handling at all: <c>PathUtils.SanitizePath</c>
/// treated the tilde as an ordinary segment, so <c>~/notes.md</c> read from a literal directory named
/// <c>~</c> inside the workspace - a file the agent never asked for and would never find. The decision
/// recorded here is that <c>ReadTool</c> resolves the real home path when it lies inside the workspace
/// and otherwise fails with a message naming <c>~</c>. Silently using a literal <c>~</c> directory is
/// not an acceptable outcome in either branch.
/// </remarks>
public sealed class ReadToolHomePathTests
{
    [Fact]
    public async Task ExecuteAsync_WithTildePath_ResolvesTheRealHomePath_WhenHomeIsInsideTheWorkspace()
    {
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        // The home directory's parent is the workspace root, so the expanded path is genuinely
        // contained without hard-coding a user name into a tracked file.
        var root = Path.GetDirectoryName(Path.GetFullPath(home));
        root.ShouldNotBeNullOrWhiteSpace();

        var fileSystem = new MockFileSystem();
        var target = Path.Combine(Path.GetFullPath(home), "notes.md");
        fileSystem.Directory.CreateDirectory(Path.GetFullPath(home));
        await fileSystem.File.WriteAllTextAsync(target, "home note");

        var tool = new ReadTool(root!, fileSystem);

        var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "~/notes.md" });

        result.Content[0].Value.ShouldBe("home note");
    }

    [Fact]
    public async Task ExecuteAsync_WithTildePath_NeverReadsALiteralTildeDirectory()
    {
        // The exact defect: a file planted under a literal '~' directory must NOT be what '~/notes.md'
        // returns. This is the assertion that would have failed before the fix.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-readtool");
        var literalTilde = Path.Combine(Path.GetFullPath(root), "~");
        fileSystem.Directory.CreateDirectory(literalTilde);
        await fileSystem.File.WriteAllTextAsync(Path.Combine(literalTilde, "notes.md"), "WRONG FILE");

        var tool = new ReadTool(root, fileSystem);

        // PathUtils surfaces containment failures as exceptions, which the agent loop renders as a
        // structured tool error. Accept either shape - what matters is that 'WRONG FILE' is never the
        // answer and that '~' is named.
        string rendered;
        try
        {
            var result = await tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "~/notes.md" });
            rendered = string.Join("\n", result.Content.Select(static c => c.Value));
        }
        catch (InvalidOperationException ex)
        {
            rendered = ex.Message;
        }

        rendered.ShouldSatisfyAllConditions(
            // '~/notes.md' must never silently resolve to a literal directory named '~' in the workspace.
            () => rendered.Contains("WRONG FILE", StringComparison.Ordinal).ShouldBeFalse(),
            // AC3: whichever branch is taken, the failure must name '~' so the agent can see why.
            () => rendered.Contains('~').ShouldBeTrue());
    }
}
