using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Paths;
using BotNexus.Tools.Utils;
using Shouldly;

namespace BotNexus.Tools.Tests.Utils;

/// <summary>
/// Pins <c>~</c> handling in <see cref="PathUtils.ResolvePath"/> (issue #3013, AC3-AC5).
/// </summary>
/// <remarks>
/// <para>
/// The decision recorded by these tests: <c>PathUtils.ResolvePath</c> <em>does</em> expand <c>~</c>,
/// and it expands <em>before</em> the containment check. Both halves matter and both are asserted:
/// </para>
/// <list type="bullet">
///   <item><description>Expanding after the check would report a legitimate in-workspace home path as
///   escaping the workspace.</description></item>
///   <item><description>Exempting expanded paths from the check would turn <c>~</c> into a
///   workspace-escape primitive - so a home directory outside the root must still throw.</description></item>
/// </list>
/// <para>
/// The previous behaviour - resolving <c>~/notes.md</c> to a literal directory named <c>~</c> inside
/// the workspace - is explicitly forbidden by <see cref="ResolvePath_NeverProducesALiteralTildeDirectory"/>.
/// </para>
/// </remarks>
public sealed class PathUtilsHomeExpansionTests
{
    [Fact]
    public void ResolvePath_ExpandsTilde_WhenHomeIsInsideTheWorkspaceRoot()
    {
        // AC4 (happy half): expansion must happen BEFORE the containment check. If the order were
        // reversed the literal '~' segment would be tested for containment and this legitimate home
        // path would be rejected.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        // Use the home directory's own parent as the workspace root, so the expanded path is genuinely
        // contained without hard-coding anyone's user name into a tracked file.
        var root = Path.GetDirectoryName(Path.GetFullPath(home));
        root.ShouldNotBeNullOrWhiteSpace();

        var resolved = PathUtils.ResolvePath("~/notes.md", root!, new MockFileSystem());

        resolved.ShouldBe(Path.GetFullPath(Path.Combine(home, "notes.md")));
    }

    [Fact]
    public void ResolvePath_Throws_WhenExpandedHomePathEscapesTheWorkspaceRoot()
    {
        // AC4 (sad half): expanding first must not exempt the result from containment. '~' is not an
        // escape hatch.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-workspace");

        var exception = Should.Throw<InvalidOperationException>(
            () => PathUtils.ResolvePath("~/notes.md", root, fileSystem));

        // AC3: the message names '~' explicitly, so the caller can see which part of their input
        // produced a path they never typed.
        exception.Message.ShouldContain("~");
    }

    [Fact]
    public void ResolvePath_NeverProducesALiteralTildeDirectory()
    {
        // The exact defect from issue #3013: '~/notes.md' used to resolve to a directory literally
        // named '~' underneath the workspace. Whichever branch we take now - expand or throw - this
        // outcome must be impossible.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-workspace");
        var literalTildeDirectory = Path.Combine(Path.GetFullPath(root), "~");

        string? resolved = null;
        try
        {
            resolved = PathUtils.ResolvePath("~/notes.md", root, fileSystem);
        }
        catch (InvalidOperationException)
        {
            // Rejecting is an acceptable outcome; silently building the '~' directory is not.
        }

        resolved?.ShouldNotStartWith(literalTildeDirectory);
    }

    [Fact]
    public void ResolvePath_ExpandsBackslashTildeForm_OnWindows()
    {
        // AC5: '~\x' must behave the same as '~/x' on Windows. On Unix a backslash is a legal
        // file-name character, so the form is deliberately left literal there and resolves as an
        // ordinary relative name inside the workspace.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(home));
        root.ShouldNotBeNullOrWhiteSpace();

        PathUtils.ResolvePath("~\\notes.md", root!, new MockFileSystem())
            .ShouldBe(Path.GetFullPath(Path.Combine(home, "notes.md")));
    }

    [Fact]
    public void ResolvePath_BareTilde_ResolvesToTheHomeDirectory()
    {
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        var root = Path.GetDirectoryName(Path.GetFullPath(home));
        root.ShouldNotBeNullOrWhiteSpace();

        PathUtils.ResolvePath("~", root!, new MockFileSystem())
            .ShouldBe(Path.GetFullPath(home));
    }

    [Fact]
    public void ResolvePath_LeavesOrdinaryRelativePathsUnchanged()
    {
        // Behaviour parity: adding expansion must not perturb the overwhelmingly common case.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-workspace");

        PathUtils.ResolvePath("docs/notes.md", root, fileSystem)
            .ShouldBe(Path.GetFullPath(Path.Combine(root, "docs", "notes.md")));
    }

    [Fact]
    public void ResolvePath_StillRejectsOrdinaryTraversalEscapes()
    {
        // Behaviour parity: the pre-existing containment guarantee is untouched.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-workspace");

        Should.Throw<Exception>(() => PathUtils.ResolvePath("../outside.md", root, fileSystem));
    }

    [Fact]
    public void ResolvePath_DoesNotExpandTildeUserForm()
    {
        // '~otheruser' is not expanded, so it stays an ordinary relative segment inside the workspace.
        var fileSystem = new MockFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "botnexus-3013-workspace");

        PathUtils.ResolvePath("~otheruser/notes.md", root, fileSystem)
            .ShouldBe(Path.GetFullPath(Path.Combine(root, "~otheruser", "notes.md")));
    }
}
