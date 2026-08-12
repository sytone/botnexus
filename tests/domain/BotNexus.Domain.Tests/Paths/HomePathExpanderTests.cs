using BotNexus.Domain.Paths;
using Shouldly;

namespace BotNexus.Domain.Tests.Paths;

/// <summary>
/// Behaviour pins for the single <c>~</c>-expansion helper (issue #3013).
/// </summary>
/// <remarks>
/// <para>
/// These tests carry the union of the four original copies' behaviour, so that deleting those copies is
/// provably a refactor and not a behaviour change. Each parity fact names the copy it came from.
/// </para>
/// <para>
/// Home-directory-dependent assertions manipulate the <c>HOME</c> environment variable rather than
/// asserting a literal path, because a literal user-home path in a tracked file would fail the
/// <c>PersonalPathLeakArchitectureTests</c> fence - and would only pass on one developer's machine.
/// </para>
/// </remarks>
public sealed class HomePathExpanderTests
{
    [Fact]
    public void Expand_LeavesPathWithoutTildeUnchanged()
    {
        // Parity: all four copies returned early on !StartsWith('~').
        HomePathExpander.Expand("relative/notes.md").ShouldBe("relative/notes.md");
    }

    [Fact]
    public void Expand_LeavesRootedPathUnchanged()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "notes.md");
        HomePathExpander.Expand(rooted).ShouldBe(rooted);
    }

    [Fact]
    public void Expand_LeavesEmptyInputUnchanged()
    {
        HomePathExpander.Expand(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void Expand_BareTilde_ReturnsHomeDirectory()
    {
        // Parity: every copy special-cased path.Length == 1.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        HomePathExpander.Expand("~").ShouldBe(home);
    }

    [Fact]
    public void Expand_TildeWithForwardSlash_ExpandsOnEveryPlatform()
    {
        // AC5: the '~/x' separator form must expand on Windows and non-Windows alike.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        HomePathExpander.Expand("~/notes.md").ShouldBe(Path.Combine(home, "notes.md"));
    }

    [Fact]
    public void Expand_TildeWithBackslash_ExpandsOnWindowsAndIsLeftLiteralElsewhere()
    {
        // AC5: the '~\x' form. On Unix a backslash is a legal file-name character, so expanding it
        // would corrupt a valid literal path - all four originals compared against
        // Path.DirectorySeparatorChar/AltDirectorySeparatorChar and therefore behaved this way.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        var expanded = HomePathExpander.Expand("~\\notes.md");

        if (OperatingSystem.IsWindows())
        {
            expanded.ShouldBe(Path.Combine(home, "notes.md"));
        }
        else
        {
            expanded.ShouldBe("~\\notes.md");
        }
    }

    [Fact]
    public void Expand_TildeUserForm_IsLeftUnchanged()
    {
        // Sad path. BotNexus has never resolved another user's home; guessing one silently would be
        // worse than leaving the literal in place. All four originals fell through to 'return path'.
        HomePathExpander.Expand("~otheruser/notes.md").ShouldBe("~otheruser/notes.md");
    }

    [Fact]
    public void Expand_TildeInMiddleOfPath_IsNotExpanded()
    {
        HomePathExpander.Expand("docs/~/notes.md").ShouldBe("docs/~/notes.md");
    }

    [Fact]
    public void GetHomeDirectory_FallsBackToHomeVariable_WhenUserProfileIsEmpty()
    {
        // Parity gap closed: LocationProbe and DefaultPathValidator had NO HOME fallback, so on Linux
        // with an empty UserProfile they produced a path rooted at the empty string. This asserts the
        // consolidated helper takes SubAgentWorkspaceRootResolver's fallback behaviour.
        // On Windows UserProfile is always populated, so the fallback is unobservable there.
        if (!string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            HomePathExpander.GetHomeDirectory()
                .ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return;
        }

        var original = Environment.GetEnvironmentVariable("HOME");
        var probe = Path.Combine(Path.GetTempPath(), "botnexus-home-probe");
        try
        {
            Environment.SetEnvironmentVariable("HOME", probe);
            HomePathExpander.GetHomeDirectory().ShouldBe(probe);
            HomePathExpander.Expand("~/notes.md").ShouldBe(Path.Combine(probe, "notes.md"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", original);
        }
    }

    [Fact]
    public void ExpandRequired_LeavesNonTildePathUnchanged_EvenWithoutAHome()
    {
        // The strict overload must not throw for input it would never have expanded.
        HomePathExpander.ExpandRequired("relative/notes.md").ShouldBe("relative/notes.md");
    }

    [Fact]
    public void ExpandRequired_MatchesExpand_WhenHomeIsKnown()
    {
        // Parity: WorldDescriptorBuilder's extra guard only fires when the home is unknown; when it is
        // known its result must be identical to the tolerant overload.
        var home = HomePathExpander.GetHomeDirectory();
        home.ShouldNotBeNullOrWhiteSpace();

        HomePathExpander.ExpandRequired("~/worlds").ShouldBe(HomePathExpander.Expand("~/worlds"));
        HomePathExpander.ExpandRequired("~").ShouldBe(home);
    }

    [Fact]
    public void StartsWithHomeToken_DistinguishesTildePathsFromOrdinaryOnes()
    {
        HomePathExpander.StartsWithHomeToken("~").ShouldBeTrue();
        HomePathExpander.StartsWithHomeToken("~/notes.md").ShouldBeTrue();
        HomePathExpander.StartsWithHomeToken("~otheruser").ShouldBeTrue();
        HomePathExpander.StartsWithHomeToken("notes.md").ShouldBeFalse();
        HomePathExpander.StartsWithHomeToken(string.Empty).ShouldBeFalse();
        HomePathExpander.StartsWithHomeToken(null).ShouldBeFalse();
    }
}
