using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Sessions.Tests;

/// <summary>
/// AC4/AC5 of #2836 at the store boundary: a file-backed session store handed a path outside the
/// verified home refuses it, and writes nothing.
/// </summary>
/// <remarks>
/// The refusal is asserted against the filesystem, not against the constructor's return. A guard
/// placed after <c>CreateDirectory</c> would throw and still have created a directory in the other
/// world's home - the store would be "refused" and the damage already done.
/// </remarks>
public sealed class FileSessionStoreHomeScopeTests
{
    // Temp-rooted rather than drive-letter literals: the validation gate runs on Linux, where a
    // "C:\..." string is a relative path and the containment check would be comparing nonsense.
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "botnexus-2836-alpha");
    private static readonly string ForeignRoot = Path.Combine(Path.GetTempPath(), "botnexus-2836-beta");
    private static readonly string ForeignPath = Path.Combine(ForeignRoot, "sessions");

    private sealed record FakeHome(string RootPath, string? WorldId) : IVerifiedHome;

    [Fact]
    public void StorePathInsideTheVerifiedHome_IsAccepted()
    {
        var fileSystem = new MockFileSystem();
        var storePath = Path.Combine(HomePath, "sessions");

        var store = Create(fileSystem, storePath, new FakeHome(HomePath, "world-a"));

        store.ShouldNotBeNull();
        fileSystem.Directory.Exists(storePath).ShouldBeTrue();
    }

    [Fact]
    public void StorePathOutsideTheVerifiedHome_IsRefusedAndWritesNothing()
    {
        var fileSystem = new MockFileSystem();

        var exception = Should.Throw<HomeScopeViolationException>(
            () => Create(fileSystem, ForeignPath, new FakeHome(HomePath, "world-a")));

        exception.HomePath.ShouldBe(Path.GetFullPath(HomePath));

        fileSystem.Directory.Exists(ForeignPath).ShouldBeFalse(
            "the refusal must happen before the store scaffolds its directory, or the guard has " +
            "already written into the world it was refusing (#2836 AC5).");
        fileSystem.AllPaths.ShouldNotContain(
            path => path.StartsWith(ForeignRoot, StringComparison.OrdinalIgnoreCase),
            "no path under the foreign home may exist after a refused construction.");
    }

    [Fact]
    public void WithoutAVerifiedHome_TheStoreBehavesExactlyAsBefore()
    {
        var fileSystem = new MockFileSystem();

        var store = Create(fileSystem, ForeignPath, home: null);

        store.ShouldNotBeNull();
        fileSystem.Directory.Exists(ForeignPath).ShouldBeTrue(
            "the guard is opt-in; a host that has not resolved a world must not start failing.");
    }

    private static FileSessionStore Create(MockFileSystem fileSystem, string storePath, IVerifiedHome? home)
        => new(
            storePath,
            NullLogger<FileSessionStore>.Instance,
            fileSystem,
            new InMemoryConversationStore(),
            redactor: null,
            home: home);
}
