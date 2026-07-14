using BotNexus.Tools.Utils;

namespace BotNexus.Tools.Tests;

/// <summary>
/// Unit coverage for <see cref="PathUtils"/> - the workspace-containment and path
/// normalization seam shared by the coding-agent file tools. Exercises both the happy
/// path (paths that resolve inside the root) and the sad path (traversal that escapes
/// the boundary, which must throw so the agent loop surfaces a structured tool error).
/// </summary>
public sealed class PathUtilsTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bnx-pathutils-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void ResolvePath_RelativeInsideRoot_ReturnsFullPathUnderRoot()
    {
        var root = NewRoot();
        try
        {
            var resolved = PathUtils.ResolvePath(Path.Combine("sub", "file.txt"), root);

            Assert.True(Path.IsPathRooted(resolved));
            Assert.StartsWith(Path.GetFullPath(root), resolved);
            Assert.EndsWith("file.txt", resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolvePath_TraversalEscapingRoot_Throws()
    {
        var root = NewRoot();
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => PathUtils.ResolvePath(Path.Combine("..", "..", "escape.txt"), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_EmptyRelative_Throws(string relative)
    {
        Assert.Throws<ArgumentException>(() => PathUtils.ResolvePath(relative, NewRoot()));
    }

    [Fact]
    public void ResolvePath_EmptyWorkingDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => PathUtils.ResolvePath("file.txt", "   "));
    }

    [Fact]
    public void SanitizePath_CollapsesDotAndDoubleDotSegments()
    {
        var sanitized = PathUtils.SanitizePath(Path.Combine("a", ".", "b", "c", "..", "d.txt"));

        var expected = Path.Combine("a", "b", "d.txt");
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizePath_TraversalBeyondStart_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => PathUtils.SanitizePath(Path.Combine("..", "x")));
    }

    [Fact]
    public void SanitizePath_NormalizesAltSeparators()
    {
        var sanitized = PathUtils.SanitizePath("a/b/c.txt");
        Assert.Equal(Path.Combine("a", "b", "c.txt"), sanitized);
    }

    [Fact]
    public void NormalizePath_RelativeWithBaseDirectory_ResolvesToAbsolute()
    {
        var root = NewRoot();
        try
        {
            var normalized = PathUtils.NormalizePath("child.txt", root);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "child.txt"), normalized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizePath_RelativeWithoutBaseDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => PathUtils.NormalizePath("child.txt"));
    }

    [Fact]
    public void GetRelativePath_ReturnsPathRelativeToBase()
    {
        var root = NewRoot();
        try
        {
            var full = Path.Combine(root, "nested", "leaf.txt");
            var relative = PathUtils.GetRelativePath(full, root);
            Assert.Equal(Path.Combine("nested", "leaf.txt"), relative);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetRelativePath_EmptyInputs_Throw()
    {
        Assert.Throws<ArgumentException>(() => PathUtils.GetRelativePath("", "base"));
        Assert.Throws<ArgumentException>(() => PathUtils.GetRelativePath("full", ""));
    }
}
