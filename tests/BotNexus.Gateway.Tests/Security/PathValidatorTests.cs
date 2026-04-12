using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Security;

public sealed class PathValidatorTests
{
    private const string Workspace = @"Q:\repos\botnexus";

    [Fact]
    public void CanRead_AllowedPath_ReturnsTrue()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus\src"]
        });

        sut.CanRead(@"Q:\repos\botnexus\src\gateway\file.cs").Should().BeTrue();
    }

    [Fact]
    public void CanRead_DeniedPath_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus"],
            DeniedPaths = [@"Q:\repos\botnexus\src\gateway\secrets"]
        });

        sut.CanRead(@"Q:\repos\botnexus\src\gateway\secrets\file.txt").Should().BeFalse();
    }

    [Fact]
    public void CanRead_OutsideAllPaths_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus\src"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\outside\file.txt").Should().BeFalse();
    }

    [Fact]
    public void CanWrite_AllowedWritePath_ReturnsTrue()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedWritePaths = [@"Q:\repos\botnexus\artifacts"]
        }, workspace: @"Q:\workspace");

        sut.CanWrite(@"Q:\repos\botnexus\artifacts\output.json").Should().BeTrue();
    }

    [Fact]
    public void CanWrite_ReadOnlyPath_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus\docs"]
        }, workspace: @"Q:\workspace");

        sut.CanWrite(@"Q:\repos\botnexus\docs\spec.md").Should().BeFalse();
    }

    [Fact]
    public void DenyOverridesAllow()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus\src"],
            AllowedWritePaths = [@"Q:\repos\botnexus\src"],
            DeniedPaths = [@"Q:\repos\botnexus\src\private"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus\src\private\secrets.txt").Should().BeFalse();
        sut.CanWrite(@"Q:\repos\botnexus\src\private\secrets.txt").Should().BeFalse();
    }

    [Fact]
    public void ValidateAndResolve_ResolvesRelativePath()
    {
        var sut = CreateValidator(policy: null);

        var resolved = sut.ValidateAndResolve(@"src\gateway\Program.cs", FileAccessMode.Read);

        resolved.Should().Be(@"Q:\repos\botnexus\src\gateway\Program.cs");
    }

    [Fact]
    public void ValidateAndResolve_ReturnsNull_WhenDenied()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus\src"],
            DeniedPaths = [@"Q:\repos\botnexus\src\gateway"]
        }, workspace: @"Q:\workspace");

        var resolved = sut.ValidateAndResolve(@"Q:\repos\botnexus\src\gateway\Program.cs", FileAccessMode.Read);

        resolved.Should().BeNull();
    }

    [Fact]
    public void ValidateAndResolve_NormalizesSlashes()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus"]
        }, workspace: @"Q:\workspace");

        var resolved = sut.ValidateAndResolve(@"Q:/repos/botnexus/src/gateway/Program.cs", FileAccessMode.Read);

        resolved.Should().Be(@"Q:\repos\botnexus\src\gateway\Program.cs");
    }

    [Fact]
    public void DefaultPolicy_WorkspaceOnly()
    {
        var nullPolicyValidator = CreateValidator(policy: null);
        var emptyPolicyValidator = CreateValidator(new FileAccessPolicy());

        nullPolicyValidator.CanRead(@"Q:\repos\botnexus\README.md").Should().BeTrue();
        nullPolicyValidator.CanRead(@"Q:\elsewhere\README.md").Should().BeFalse();
        emptyPolicyValidator.CanWrite(@"Q:\repos\botnexus\artifacts\output.txt").Should().BeTrue();
        emptyPolicyValidator.CanWrite(@"Q:\elsewhere\output.txt").Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitive_OnWindows()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\Repos\BotNexus"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"q:\repos\botnexus\src\gateway\Program.cs").Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public void SubdirectoryMatch()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus\src").Should().BeTrue();
    }

    [Fact]
    public void PartialNameNoMatch()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus-other\src").Should().BeFalse();
    }

    [Fact]
    public void GlobStar_MatchesAllUnderDirectory()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\*"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus\file.cs").Should().BeTrue();
    }

    [Fact]
    public void GlobDoubleStar_MatchesRecursive()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\**\*.cs"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus\src\gateway\Program.cs").Should().BeTrue();
        sut.CanRead(@"Q:\repos\botnexus\src\gateway\file.txt").Should().BeFalse();
    }

    [Fact]
    public void GlobInDeny_BlocksPattern()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\repos\botnexus"],
            DeniedPaths = [@"**\*.env"]
        });

        sut.CanRead(@"Q:\repos\botnexus\src\.env").Should().BeFalse();
        sut.CanRead(@"Q:\repos\botnexus\config\production.env").Should().BeFalse();
        sut.CanRead(@"Q:\repos\botnexus\src\Program.cs").Should().BeTrue();
    }

    [Fact]
    public void GlobAndLiteral_BothWork()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths =
            [
                @"Q:\repos\botnexus\docs",
                @"Q:\repos\**\*.cs"
            ]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\botnexus\docs\spec.md").Should().BeTrue();
        sut.CanRead(@"Q:\repos\botnexus\src\Program.cs").Should().BeTrue();
        sut.CanRead(@"Q:\repos\botnexus\src\readme.md").Should().BeFalse();
    }

    [Fact]
    public void GlobNoMatch_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [@"Q:\other\*"]
        }, workspace: @"Q:\workspace");

        sut.CanRead(@"Q:\repos\file.cs").Should().BeFalse();
    }

    private static DefaultPathValidator CreateValidator(FileAccessPolicy? policy, string workspace = Workspace)
        => new(policy, workspace);
}
