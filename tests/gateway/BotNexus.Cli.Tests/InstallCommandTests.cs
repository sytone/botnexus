using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for InstallCommand git clone argument hardening: the repo spec must be
/// terminated with <c>--</c> so a value beginning with '-' cannot be reinterpreted
/// as a git option, and such a value must be rejected up front as defense-in-depth.
/// </summary>
public sealed class InstallCommandTests
{
    [Fact]
    public void BuildCloneArguments_TerminatesRepoSpecWithDoubleDash()
    {
        // Act
        var args = InstallCommand.BuildCloneArguments("https://github.com/sytone/botnexus.git", "/home/user/botnexus");

        // Assert - the option terminator must precede the repo spec, and --no-local
        // forces reachable-object transport for local sources (see #1910)
        args.ShouldBe("clone --no-local -- \"https://github.com/sytone/botnexus.git\" \"/home/user/botnexus\"");
    }

    [Fact]
    public void BuildCloneArguments_UsesNoLocalToAvoidObjectStoreByteCopy()
    {
        // Act
        var args = InstallCommand.BuildCloneArguments("/tmp/source", "/tmp/target");

        // Assert - --no-local must appear before the -- terminator so git negotiates a
        // reachable pack rather than copying the entire objects/ dir across volumes
        args.ShouldContain("--no-local");
        args.IndexOf("--no-local").ShouldBeLessThan(args.IndexOf(" -- "));
    }

    [Fact]
    public void ValidateRepo_RepoStartingWithDash_IsRejected()
    {
        // Act
        var error = InstallCommand.ValidateRepo("--upload-pack=evil");

        // Assert
        error.ShouldNotBeNull();
        error.ShouldContain("-");
    }

    [Fact]
    public void ValidateRepo_NormalRepoSpec_IsAccepted()
    {
        // Act
        var error = InstallCommand.ValidateRepo("https://github.com/sytone/botnexus.git");

        // Assert
        error.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RepoStartingWithDash_ReturnsErrorWithoutCloning()
    {
        // Arrange - a target that does not already contain a .git dir
        var tempTarget = Path.Combine(Path.GetTempPath(), $"botnexus-install-test-{Guid.NewGuid():N}");

        // Act
        var exitCode = await InstallCommand.ExecuteAsync(tempTarget, "-evil", build: false, verbose: false, CancellationToken.None);

        // Assert
        exitCode.ShouldNotBe(0);
        Directory.Exists(tempTarget).ShouldBeFalse();
    }
}
