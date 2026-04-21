using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

public sealed class PathValidatorTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "repos", "botnexus");
    private static readonly string TestRepoRoot = Path.Combine(Path.GetTempPath(), "repos", "botnexus");
    private static readonly string TestWorkspace = Path.Combine(Path.GetTempPath(), "workspace");

    [Fact]
    public void CanRead_AllowedPath_ReturnsTrue()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(TestRepoRoot, "src")]
        });

        sut.CanRead(Path.Combine(TestRepoRoot, "src", "gateway", "file.cs")).ShouldBeTrue();
    }

    [Fact]
    public void CanRead_DeniedPath_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [TestRepoRoot],
            DeniedPaths = [Path.Combine(TestRepoRoot, "src", "gateway", "secrets")]
        });

        sut.CanRead(Path.Combine(TestRepoRoot, "src", "gateway", "secrets", "file.txt")).ShouldBeFalse();
    }

    [Fact]
    public void CanRead_OutsideAllPaths_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(TestRepoRoot, "src")]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(Path.GetTempPath(), "outside", "file.txt")).ShouldBeFalse();
    }

    [Fact]
    public void CanWrite_AllowedWritePath_ReturnsTrue()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedWritePaths = [Path.Combine(TestRepoRoot, "artifacts")]
        }, workspace: TestWorkspace);

        sut.CanWrite(Path.Combine(TestRepoRoot, "artifacts", "output.json")).ShouldBeTrue();
    }

    [Fact]
    public void CanWrite_ReadOnlyPath_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(TestRepoRoot, "docs")]
        }, workspace: TestWorkspace);

        sut.CanWrite(Path.Combine(TestRepoRoot, "docs", "spec.md")).ShouldBeFalse();
    }

    [Fact]
    public void DenyOverridesAllow()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(TestRepoRoot, "src")],
            AllowedWritePaths = [Path.Combine(TestRepoRoot, "src")],
            DeniedPaths = [Path.Combine(TestRepoRoot, "src", "private")]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "src", "private", "secrets.txt")).ShouldBeFalse();
        sut.CanWrite(Path.Combine(TestRepoRoot, "src", "private", "secrets.txt")).ShouldBeFalse();
    }

    [Fact]
    public void ValidateAndResolve_ResolvesRelativePath()
    {
        var sut = CreateValidator(policy: null);

        var resolved = sut.ValidateAndResolve(Path.Combine("src", "gateway", "Program.cs"), FileAccessMode.Read);

        resolved.ShouldBe(Path.Combine(TestRepoRoot, "src", "gateway", "Program.cs"));
    }

    [Fact]
    public void ValidateAndResolve_ReturnsNull_WhenDenied()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(TestRepoRoot, "src")],
            DeniedPaths = [Path.Combine(TestRepoRoot, "src", "gateway")]
        }, workspace: TestWorkspace);

        var resolved = sut.ValidateAndResolve(Path.Combine(TestRepoRoot, "src", "gateway", "Program.cs"), FileAccessMode.Read);

        resolved.ShouldBeNull();
    }

    [Fact]
    public void ValidateAndResolve_NormalizesSlashes()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [TestRepoRoot]
        }, workspace: TestWorkspace);

        // Use forward slashes and then expect them to be normalized to the platform's directory separator
        var testPath = TestRepoRoot.Replace(Path.DirectorySeparatorChar, '/') + "/src/gateway/Program.cs";
        var resolved = sut.ValidateAndResolve(testPath, FileAccessMode.Read);

        resolved.ShouldBe(Path.Combine(TestRepoRoot, "src", "gateway", "Program.cs"));
    }

    [Fact]
    public void DefaultPolicy_WorkspaceOnly()
    {
        var nullPolicyValidator = CreateValidator(policy: null);
        var emptyPolicyValidator = CreateValidator(new FileAccessPolicy());

        nullPolicyValidator.CanRead(Path.Combine(TestRepoRoot, "README.md")).ShouldBeTrue();
        nullPolicyValidator.CanRead(Path.Combine(Path.GetTempPath(), "elsewhere", "README.md")).ShouldBeFalse();
        emptyPolicyValidator.CanWrite(Path.Combine(TestRepoRoot, "artifacts", "output.txt")).ShouldBeTrue();
        emptyPolicyValidator.CanWrite(Path.Combine(Path.GetTempPath(), "elsewhere", "output.txt")).ShouldBeFalse();
    }

    [Fact]
    public void CaseInsensitive_OnWindows()
    {
        var testPath = Path.Combine(TestRepoRoot.Replace("repos", "Repos").Replace("botnexus", "BotNexus"));
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [testPath]
        }, workspace: TestWorkspace);

        var lowerPath = Path.Combine(TestRepoRoot, "src", "gateway", "Program.cs");
        sut.CanRead(lowerPath).ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void SubdirectoryMatch()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [TestRepoRoot]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "src")).ShouldBeTrue();
    }

    [Fact]
    public void PartialNameNoMatch()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [TestRepoRoot]
        }, workspace: TestWorkspace);

        var otherPath = Path.Combine(Path.GetDirectoryName(TestRepoRoot)!, "botnexus-other", "src");
        sut.CanRead(otherPath).ShouldBeFalse();
    }

    [Fact]
    public void GlobStar_MatchesAllUnderDirectory()
    {
        var repoParent = Path.GetDirectoryName(TestRepoRoot)!;
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(repoParent, "*")]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "file.cs")).ShouldBeTrue();
    }

    [Fact]
    public void GlobDoubleStar_MatchesRecursive()
    {
        var repoParent = Path.GetDirectoryName(TestRepoRoot)!;
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(repoParent, "**", "*.cs")]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "src", "gateway", "Program.cs")).ShouldBeTrue();
        sut.CanRead(Path.Combine(TestRepoRoot, "src", "gateway", "file.txt")).ShouldBeFalse();
    }

    [Fact]
    public void GlobInDeny_BlocksPattern()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [TestRepoRoot],
            DeniedPaths = [Path.Combine("**", "*.env")]
        });

        sut.CanRead(Path.Combine(TestRepoRoot, "src", ".env")).ShouldBeFalse();
        sut.CanRead(Path.Combine(TestRepoRoot, "config", "production.env")).ShouldBeFalse();
        sut.CanRead(Path.Combine(TestRepoRoot, "src", "Program.cs")).ShouldBeTrue();
    }

    [Fact]
    public void GlobAndLiteral_BothWork()
    {
        var repoParent = Path.GetDirectoryName(TestRepoRoot)!;
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths =
            [
                Path.Combine(TestRepoRoot, "docs"),
                Path.Combine(repoParent, "**", "*.cs")
            ]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "docs", "spec.md")).ShouldBeTrue();
        sut.CanRead(Path.Combine(TestRepoRoot, "src", "Program.cs")).ShouldBeTrue();
        sut.CanRead(Path.Combine(TestRepoRoot, "src", "readme.md")).ShouldBeFalse();
    }

    [Fact]
    public void GlobNoMatch_ReturnsFalse()
    {
        var sut = CreateValidator(new FileAccessPolicy
        {
            AllowedReadPaths = [Path.Combine(Path.GetTempPath(), "other", "*")]
        }, workspace: TestWorkspace);

        sut.CanRead(Path.Combine(TestRepoRoot, "file.cs")).ShouldBeFalse();
    }

    private static DefaultPathValidator CreateValidator(FileAccessPolicy? policy, string? workspace = null)
        => new(policy, workspace ?? Workspace);
}