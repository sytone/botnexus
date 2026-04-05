using BotNexus.CodingAgent.Utils;
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

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
