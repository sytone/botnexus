using BotNexus.Tools.Utils;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Utils;

public sealed class PathUtilsTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-{Guid.NewGuid():N}");

    public PathUtilsTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public void ResolvePath_ReturnsPathWithinWorkingDirectory()
    {
        var resolved = PathUtils.ResolvePath(Path.Combine("src", "file.txt"), _workingDirectory);

        resolved.Should().Be(Path.Combine(_workingDirectory, "src", "file.txt"));
    }

    [Fact]
    public void ResolvePath_WhenTraversalEscapesRoot_Throws()
    {
        var action = () => PathUtils.ResolvePath("..\\outside.txt", _workingDirectory);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Path traversal is not allowed*");
    }

    [Fact]
    public void SanitizePath_NormalizesSegments()
    {
        var sanitized = PathUtils.SanitizePath(Path.Combine("folder", "child", "..", "file.txt"));

        sanitized.Should().Be(Path.Combine("folder", "file.txt"));
    }

    [Fact]
    public void GetRelativePath_ComputesRelativePath()
    {
        var fullPath = Path.Combine(_workingDirectory, "nested", "target.txt");
        var relative = PathUtils.GetRelativePath(fullPath, _workingDirectory);

        relative.Should().Be(Path.Combine("nested", "target.txt"));
    }

    [Fact]
    public void ResolvePath_WhenPathTraversesViaSymlinkOutsideRoot_ThrowsUnauthorizedAccessException()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-outside-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(_workingDirectory, "escape-link");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(symlinkPath, outsideDirectory);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or PlatformNotSupportedException ||
                ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var action = () => PathUtils.ResolvePath(Path.Combine("escape-link", "secret.txt"), _workingDirectory);

            action.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvePath_WhenPathIsOutsideSymlinkRoot_ThrowsUnauthorizedAccessException()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-outside-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(_workingDirectory, "escape-link-root");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(symlinkPath, outsideDirectory);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or PlatformNotSupportedException ||
                ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var action = () => PathUtils.ResolvePath("escape-link-root", _workingDirectory);

            action.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    // ───────────────────────────────────────────────
    //  NormalizePath tests
    // ───────────────────────────────────────────────

    [Fact]
    public void NormalizePath_WithAbsolutePath_ReturnsNormalizedPath()
    {
        var input = Path.Combine(_workingDirectory, "src", "sub", "..", "file.txt");
        var expected = Path.Combine(_workingDirectory, "src", "file.txt");

        var result = PathUtils.NormalizePath(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizePath_WithRelativePathAndBaseDirectory_ResolvesCorrectly()
    {
        var relativePath = Path.Combine("src", "file.txt");
        var expected = Path.Combine(_workingDirectory, "src", "file.txt");

        var result = PathUtils.NormalizePath(relativePath, _workingDirectory);

        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizePath_WithRelativePathWithoutBaseDirectory_Throws()
    {
        var action = () => PathUtils.NormalizePath(Path.Combine("src", "file.txt"));

        action.Should().Throw<ArgumentException>()
            .WithMessage("*base directory*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePath_WithEmptyPath_Throws(string? path)
    {
        var action = () => PathUtils.NormalizePath(path!);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Path cannot be empty*");
    }

    [Fact]
    public void NormalizePath_WithOutOfWorkspacePath_DoesNotThrow()
    {
        // Create a second temp directory that is definitively outside _workingDirectory.
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            var outsidePath = Path.Combine(outsideDirectory, "some", "file.txt");

            // NormalizePath should NOT throw for out-of-workspace paths (unlike ResolvePath).
            var result = PathUtils.NormalizePath(outsidePath);

            result.Should().Be(outsidePath);
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    // ───────────────────────────────────────────────
    //  GetGitIgnoredPaths tests
    // ───────────────────────────────────────────────

    [Fact]
    public void GetGitIgnoredPaths_WithOutOfWorkspacePaths_DoesNotThrow()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            var outsidePaths = new[]
            {
                Path.Combine(outsideDirectory, "file1.txt"),
                Path.Combine(outsideDirectory, "sub", "file2.txt"),
            };

            // Out-of-workspace paths must not throw (the bug that was fixed).
            var ignored = PathUtils.GetGitIgnoredPaths(outsidePaths, _workingDirectory);

            // Out-of-workspace paths are never reported as git-ignored by this workspace.
            ignored.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetGitIgnoredPaths_WithMixedWorkspaceAndOutOfWorkspacePaths_ProcessesWorkspaceOnly()
    {
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-pathutils-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            var workspacePath = Path.Combine(_workingDirectory, "src", "app.cs");
            var outsidePath = Path.Combine(outsideDirectory, "external.cs");

            var allPaths = new[] { workspacePath, outsidePath };

            // Should not throw, and out-of-workspace paths must not appear in the ignored set.
            var ignored = PathUtils.GetGitIgnoredPaths(allPaths, _workingDirectory);

            ignored.Should().NotContain(outsidePath,
                "out-of-workspace paths should never appear in the ignored set");
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetGitIgnoredPaths_WithEmptyPaths_ReturnsEmptySet()
    {
        var paths = new[] { "", "   ", null! };

        var ignored = PathUtils.GetGitIgnoredPaths(paths, _workingDirectory);

        ignored.Should().BeEmpty();
    }

    [Fact]
    public void GetGitIgnoredPaths_WithOnlyWorkspacePaths_StillWorks()
    {
        // Regression test: existing behaviour for workspace-only paths must be preserved.
        var workspacePaths = new[]
        {
            Path.Combine(_workingDirectory, "src", "file1.cs"),
            Path.Combine(_workingDirectory, "tests", "file2.cs"),
        };

        // The temp directory is not a git repo, so git check-ignore will exit quickly.
        // We only assert the call completes without throwing.
        var action = () => PathUtils.GetGitIgnoredPaths(workspacePaths, _workingDirectory);

        action.Should().NotThrow();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
