using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the #2392 secret-file permission guard-rail.
///
/// <para><b>What it pins.</b> Every source file that writes a secret-bearing file
/// (<c>config.json</c>, <c>auth.json</c>, or a full backup copy of config.json) must route that
/// write through the single central helper <c>SecureFilePermissions.RestrictToOwner</c>. The
/// alternative - trusting each author to remember a chmod/ACL call - is precisely how the gap in
/// #2392 arose in the first place: a repo-wide scan found ZERO uses of <c>UnixFileMode</c>,
/// <c>SetUnixFileMode</c>, <c>FileSecurity</c> or <c>SetAccessControl</c> anywhere under
/// <c>src/</c>.</para>
///
/// <para><b>And the inverse.</b> The fence also forbids call sites from hand-rolling
/// <c>SetUnixFileMode</c> / <c>SetAccessControl</c> directly, because a raw
/// <c>File.SetUnixFileMode</c> throws <see cref="PlatformNotSupportedException"/> on Windows and a
/// raw ACL call does not compile portably - i.e. the one-sided "fix" that silently secures only one
/// OS. Only the central helper (which handles both worlds and is behaviourally tested against real
/// files on the executing platform) is permitted to touch those APIs.</para>
///
/// <para>Source-text based, like <see cref="SecretRedactionFenceArchitectureTests"/>: "this write
/// path narrows its permissions" is not reliably observable by reflection.</para>
/// </summary>
public sealed class SecretFilePermissionFenceArchitectureTests : ArchitectureTest
{

    private const string SecureFilePermissionsSource =
        "src/gateway/BotNexus.Gateway.Configuration/SecureFilePermissions.cs";

    /// <summary>
    /// Source files that write a secret-bearing file and must therefore call the central helper.
    /// Adding a new secret-writing seam means adding it here.
    /// </summary>
    private static readonly string[] SecretWritingSurfaces =
    {
        // Atomic temp-file + move rewrite of config.json (provider API keys, channel bot tokens).
        //
        // #3527: the write moved out of PlatformConfigWriter into the JSON writer backend. The file
        // that PERFORMS the write is what this fence must track - naming the caller would leave the
        // permission call unguarded the moment it moved again.
        "src/gateway/BotNexus.Gateway.Configuration/Writers/JsonConfigurationWriter.cs",
        // Byte-for-byte backup copies of config.json, secrets included.
        "src/gateway/BotNexus.Gateway.Configuration/ConfigBackupService.cs",
        // auth.json - OAuth refresh/access tokens (gateway side).
        "src/gateway/BotNexus.Gateway.Configuration/GatewayAuthManager.cs",
        // auth.json - OAuth token persist + refresh (CLI side).
        "src/gateway/BotNexus.Cli/Commands/ProviderCommand.cs",
        "src/gateway/BotNexus.Cli/Commands/Provider/CopilotAuthLoader.cs",
        // secrets.db - the sqlite: secret store, written by `botnexus secret set`.
        "src/gateway/BotNexus.Cli/Commands/SecretCommand.cs",
        // config.db - the SQLite configuration store, a full copy of every config.json value
        // including provider API keys and channel bot tokens, plus its WAL/SHM sidecars (#3414).
        // Narrowed inside the store rather than at its five construction sites, so the seam that
        // must be pinned is the store itself.
        "src/gateway/BotNexus.Gateway.Configuration/Store/SqliteConfigStore.cs",
    };

    /// <summary>A call to the central helper, in either overload form.</summary>
    private static readonly Regex RestrictCall =
        new(@"SecureFilePermissions\s*\.\s*RestrictToOwner\s*\(", RegexOptions.Compiled);

    /// <summary>Raw platform permission APIs that only the central helper may use.</summary>
    private static readonly Regex RawPermissionApi =
        new(@"\b(SetUnixFileMode|SetAccessControl|SetAccessRuleProtection)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void CentralHelper_Exists()
    {
        var path = ResolvePath(SecureFilePermissionsSource);
        File.Exists(path).ShouldBeTrue(
            "The central owner-only file-permission helper is missing. Every secret-bearing write " +
            $"seam depends on it (#2392). Expected at: {path}");
    }

    [Fact]
    public void AllSecretWritingSurfaces_Exist()
    {
        foreach (var relative in SecretWritingSurfaces)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                "Expected secret-writing surface source not found: " + path +
                "\nIf this file was renamed or removed, update SecretWritingSurfaces - do not delete " +
                "the entry without confirming the write seam is gone. See #2392.");
        }
    }

    [Fact]
    public void EverySecretWritingSurface_RoutesThroughCentralHelper()
    {
        foreach (var relative in SecretWritingSurfaces)
        {
            var path = ResolvePath(relative);
            var source = File.ReadAllText(path);

            RestrictCall.IsMatch(source).ShouldBeTrue(
                $"'{relative}' writes a secret-bearing file (config.json / auth.json / a config backup) " +
                "but never calls SecureFilePermissions.RestrictToOwner. Without it the file inherits the " +
                "process umask on Linux/macOS (group- and world-readable under the default umask 022) and " +
                "the parent directory ACL on Windows, leaving provider API keys and OAuth tokens exposed " +
                $"to every other local account. See #2392.\nFile: {path}");
        }
    }

    [Fact]
    public void OnlyCentralHelper_UsesRawPlatformPermissionApis()
    {
        var helperPath = Path.GetFullPath(ResolvePath(SecureFilePermissionsSource));
        var srcRoot = Path.Combine(Repository.Root, "src");

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), helperPath, StringComparison.OrdinalIgnoreCase))
            .Where(file => RawPermissionApi.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(Repository.Root, file).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These files call a raw platform permission API directly instead of going through " +
            "SecureFilePermissions.RestrictToOwner: " + string.Join(", ", offenders) +
            ".\nFile.SetUnixFileMode THROWS PlatformNotSupportedException on Windows, and the ACL APIs " +
            "are Windows-only, so a hand-rolled call is a fix that works on one OS and breaks or " +
            "silently does nothing on the other. Route it through the central helper, which handles " +
            "both worlds and is behaviourally tested on each. See #2392.");
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsUnsecuredWriteAndRawApiUse()
    {
        // Synthetic regression: a config write seam that never narrows permissions.
        const string unsecuredWriter = """
            public sealed class FakeConfigWriter
            {
                public async Task WriteAsync(string path, string json)
                {
                    await File.WriteAllTextAsync(path, json);
                    File.Move(temp, path, overwrite: true);
                }
            }
            """;
        RestrictCall.IsMatch(unsecuredWriter).ShouldBeFalse(
            "Vacuity guard: a writer that never calls RestrictToOwner must NOT match the detector. " +
            "If this fails, the detector is too loose and the surface fence passes vacuously.");

        // Synthetic regression: a hand-rolled POSIX-only chmod that no-ops/throws on Windows.
        const string rawApiUser = """
            public static class FakeSecurer
            {
                public static void Secure(string path)
                    => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            """;
        RawPermissionApi.IsMatch(rawApiUser).ShouldBeTrue(
            "Vacuity guard: a hand-rolled SetUnixFileMode call MUST be detected as a raw-API offender. " +
            "If this fails, the raw-API fence passes vacuously and a Windows-breaking fix ships.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsSecuredWriteSeam()
    {
        const string securedWriter = """
            public sealed class FakeConfigWriter
            {
                public async Task WriteAsync(string path, string json)
                {
                    await File.WriteAllTextAsync(temp, json);
                    SecureFilePermissions.RestrictToOwner(_fileSystem, temp);
                    File.Move(temp, path, overwrite: true);
                    SecureFilePermissions.RestrictToOwner(_fileSystem, path);
                }
            }
            """;
        RestrictCall.IsMatch(securedWriter).ShouldBeTrue(
            "Positive pin: a writer that routes through RestrictToOwner must be accepted. " +
            "If this fails, the detector is over-tight.");
        RawPermissionApi.IsMatch(securedWriter).ShouldBeFalse(
            "Positive pin: routing through the central helper must NOT be flagged as raw-API use.");
    }

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

}
