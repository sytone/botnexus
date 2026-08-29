using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the file-per-secret store's owner-only write path (#3528 AC4).
/// </summary>
/// <remarks>
/// <para><b>Why a separate fence file.</b> The substance is the same guard-rail as
/// <see cref="SecretFilePermissionFenceArchitectureTests"/> - a secret-bearing write must route
/// through <c>SecureFilePermissions.RestrictToOwner</c> - and the new surface belongs in that class's
/// <c>SecretWritingSurfaces</c> list. It is pinned here instead because that file is concurrently
/// being edited by the #3414 fix; two branches rewriting the same array is how a fence entry gets
/// lost in a merge. <b>Fold this into the main list when #3623 lands and delete this file.</b></para>
/// <para><b>Why the store needs the fence at all.</b> #3414 is the cautionary case: <c>config.db</c>
/// was a secret store added AFTER the #2392 seam existed, and it simply did not call it. Nothing
/// failed, because nothing was watching. A new secret store shipping without a fence entry repeats
/// that exactly.</para>
/// </remarks>
public sealed class FileSecretStorePermissionFenceArchitectureTests : ArchitectureTest
{
    private const string FileSecretStoreSource =
        "src/gateway/BotNexus.Gateway.Configuration/FileSecretStore.cs";

    private static readonly Regex RestrictCall =
        new(@"SecureFilePermissions\s*\.\s*RestrictToOwner\s*\(", RegexOptions.Compiled);

    /// <summary>Any call that writes file content, which is what must be followed by the narrowing.</summary>
    private static readonly Regex ContentWrite =
        new(@"\.(WriteAllText|WriteAllTextAsync|WriteAllBytes|WriteAllBytesAsync|Copy|Move)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void FileSecretStore_Exists()
    {
        var path = ResolvePath(FileSecretStoreSource);
        File.Exists(path).ShouldBeTrue(
            "The file-per-secret store is missing. If it was renamed, update this fence rather than " +
            $"deleting it - the owner-only guarantee of #3528 depends on it. Expected at: {path}");
    }

    [Fact]
    public void FileSecretStore_RoutesItsWriteThroughTheCentralHelper()
    {
        var path = ResolvePath(FileSecretStoreSource);
        var source = File.ReadAllText(path);

        ContentWrite.IsMatch(source).ShouldBeTrue(
            "Vacuity guard: FileSecretStore no longer contains any file-content write, so asserting " +
            "that its write is secured would prove nothing. If the write moved to another file, this " +
            "fence must follow it there - see the #3527 note in SecretFilePermissionFenceArchitectureTests.");

        RestrictCall.IsMatch(source).ShouldBeTrue(
            "FileSecretStore writes user secrets to disk but never calls " +
            "SecureFilePermissions.RestrictToOwner. Without it every secret file inherits the process " +
            "umask on Linux/macOS - group- and world-readable under the default umask 022 - and the " +
            "parent directory ACL on Windows, so every other local account can read every stored " +
            $"secret. This is the #3414 failure mode repeated on a brand-new store. See #2392, #3528.\nFile: {path}");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsAnUnsecuredSecretWrite()
    {
        const string unsecuredStore = """
            public sealed class FakeSecretStore
            {
                public void Set(string path, string value)
                    => _fileSystem.File.WriteAllText(path, value);
            }
            """;

        ContentWrite.IsMatch(unsecuredStore).ShouldBeTrue(
            "Vacuity guard: a plain WriteAllText must be detected as a content write, otherwise the " +
            "write-present precondition above passes for a file that writes nothing.");
        RestrictCall.IsMatch(unsecuredStore).ShouldBeFalse(
            "Vacuity guard: a store that never narrows permissions must NOT match the detector. If " +
            "this fails the detector is too loose and the fence passes vacuously.");
    }

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));
}
