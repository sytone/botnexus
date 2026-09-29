using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class DocumentationRootResolverTests
{
    [Fact]
    public void Resolve_UsesOverrideBeforeSourceAndInstalledRoots()
    {
        var fs = CreateFileSystem("/override", "/source", "/installed");

        var result = DocumentationRootResolver.Resolve(fs, "/override", "/source/child", "/installed");

        result.IsResolved.ShouldBeTrue();
        result.Strategy.ShouldBe(DocumentationRootStrategy.Override);
        result.DocsPath.ShouldBe(Path.GetFullPath("/override/docs"));
    }

    [Fact]
    public void Resolve_WalksUpFromSourceCheckoutBeforeTryingInstalledRoot()
    {
        var fs = CreateFileSystem("/source", "/installed");

        var result = DocumentationRootResolver.Resolve(fs, null, "/source/src/gateway/bin", "/installed");

        result.Strategy.ShouldBe(DocumentationRootStrategy.SourceCheckout);
        result.DocsPath.ShouldBe(Path.GetFullPath("/source/docs"));
    }

    [Fact]
    public void Resolve_UsesInstalledRootAfterSourceCheckoutMisses()
    {
        var fs = CreateFileSystem("/installed");

        var result = DocumentationRootResolver.Resolve(fs, null, "/unrelated/path", "/installed");

        result.Strategy.ShouldBe(DocumentationRootStrategy.InstalledRelease);
        result.DocsPath.ShouldBe(Path.GetFullPath("/installed/docs"));
    }

    [Fact]
    public void Resolve_RejectsUnrelatedRepositoryBeforeInstalledRoot()
    {
        var fs = CreateFileSystem("/installed");
        fs.AddDirectory("/unrelated/docs");
        fs.AddDirectory("/unrelated/src");
        fs.AddFile("/unrelated/README.md", new MockFileData("untrusted repository"));

        var result = DocumentationRootResolver.Resolve(fs, null, "/unrelated/src/bin", "/installed");

        result.Strategy.ShouldBe(DocumentationRootStrategy.InstalledRelease);
        result.DocsPath.ShouldBe(Path.GetFullPath("/installed/docs"));
    }

    [Fact]
    public void Resolve_ReturnsClearUnresolvedResult_WhenNoStrategyMatches()
    {
        var result = DocumentationRootResolver.Resolve(new MockFileSystem(), null, "/missing", "/also-missing");

        result.IsResolved.ShouldBeFalse();
        result.Strategy.ShouldBe(DocumentationRootStrategy.Unresolved);
        result.DocsPath.ShouldBeNull();
        result.Message.ShouldContain("Documentation is not available on this installation");
    }

    private static MockFileSystem CreateFileSystem(params string[] roots)
    {
        var fs = new MockFileSystem();
        foreach (var root in roots)
        {
            fs.AddDirectory(Path.Combine(root, "docs"));
            fs.AddFile(Path.Combine(root, "BotNexus.slnx"), new MockFileData(string.Empty));
        }
        return fs;
    }
}
