using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Regression coverage for issue #2406: <c>Path.GetFullPath</c> collapses <c>..</c>
/// lexically with no link resolution, so <c>&lt;symlink&gt;/../&lt;target&gt;</c> could be
/// approved as in-workspace while the operating system resolves the link first and
/// lands outside the sandbox root.
/// </summary>
public sealed class PathValidatorSymlinkEscapeTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _outside;

    public PathValidatorSymlinkEscapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bn-2406-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "workspace");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void ValidateAndResolve_SymlinkParentTraversalEscapingRoot_IsRejected()
    {
        // ws/link -> <root>/outside/deep ; "link/../secret.txt" collapses lexically to
        // ws/secret.txt (looks safe) but the OS resolves to <root>/outside/secret.txt.
        var linkTarget = Path.Combine(_outside, "deep");
        Directory.CreateDirectory(linkTarget);
        if (!TryCreateDirectorySymlink(Path.Combine(_workspace, "link"), linkTarget))
        {
            return;
        }

        var sut = CreateValidator();

        sut.ValidateAndResolve(Path.Combine("link", "..", "secret.txt"), FileAccessMode.Read)
            .ShouldBeNull();
        sut.ValidateAndResolve(Path.Combine("link", "..", "secret.txt"), FileAccessMode.Write)
            .ShouldBeNull();
    }

    [Fact]
    public void ValidateAndResolve_SymlinkDirectlyEscapingRoot_IsRejected()
    {
        if (!TryCreateDirectorySymlink(Path.Combine(_workspace, "escape"), _outside))
        {
            return;
        }

        var sut = CreateValidator();

        sut.ValidateAndResolve(Path.Combine("escape", "secret.txt"), FileAccessMode.Read)
            .ShouldBeNull();
    }

    [Fact]
    public void ValidateAndResolve_SymlinkResolvingInsideRoot_IsAllowed()
    {
        // False-rejection guard: legitimate in-workspace links must keep working.
        var inner = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "ok.txt"), "ok");
        if (!TryCreateDirectorySymlink(Path.Combine(_workspace, "alias"), inner))
        {
            return;
        }

        var sut = CreateValidator();

        sut.ValidateAndResolve(Path.Combine("alias", "ok.txt"), FileAccessMode.Read)
            .ShouldNotBeNull();
        sut.ValidateAndResolve(Path.Combine("alias", "..", "data", "ok.txt"), FileAccessMode.Read)
            .ShouldNotBeNull();
    }

    [Fact]
    public void ValidateAndResolve_OrdinaryInWorkspacePaths_StillResolve()
    {
        var sut = CreateValidator();

        sut.ValidateAndResolve("notes.txt", FileAccessMode.Write)
            .ShouldBe(Path.Combine(_workspace, "notes.txt"));
        sut.ValidateAndResolve(Path.Combine("a", "b", "..", "c.txt"), FileAccessMode.Read)
            .ShouldBe(Path.Combine(_workspace, "a", "c.txt"));
        sut.ValidateAndResolve(Path.Combine("..", "outside", "secret.txt"), FileAccessMode.Read)
            .ShouldBeNull();
    }

    private DefaultPathValidator CreateValidator() => new(policy: null, _workspace);

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.ResolveLinkTarget(link, returnFinalTarget: true) is not null;
        }
        catch (IOException)
        {
            // Windows without Developer Mode / elevation cannot create symlinks.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
