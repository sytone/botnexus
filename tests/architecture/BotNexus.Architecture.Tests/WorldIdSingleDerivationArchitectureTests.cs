using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the world-identity token's <b>single derivation</b> (#2834,
/// acceptance criterion 5).
///
/// <para><b>Why this is a fence and not a unit test.</b> The world ID exists so a store can assert
/// "you are not my world". Its whole value rests on there being exactly ONE derivation: if a store or
/// a scheduler re-read <c>worldId</c> from configuration on its own, a broken resolver would produce
/// the same wrong answer in the identity path and in the path-resolution path at once, both would
/// agree, and the guard would pass while the data was still wrong. That one-value-two-derivations
/// shape is the recurring defect family behind #2796, #2792, #2748 and #2793. "Nobody else reads the
/// key" is a property of the whole source tree, so only a categorical scan can hold it.</para>
///
/// <para>Consumers take the injected <c>WorldId</c> dependency. The single legal reader of the raw
/// <c>worldId</c> configuration key is the resolver itself.</para>
/// </summary>
public sealed class WorldIdSingleDerivationArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The only files permitted to mention the raw <c>worldId</c> configuration key: the resolver that
    /// defines the single derivation, the config POCO that declares the property so it reaches the
    /// schema and the settings UI, and the bootstrap writer that persists the already-resolved value.
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        "src/gateway/BotNexus.Gateway.Configuration/WorldId.cs",
        "src/gateway/BotNexus.Gateway.Configuration/PlatformConfig.cs",
    ];

    /// <summary>
    /// Matches a raw read of the root <c>worldId</c> configuration key.
    /// <para>Deliberately the literal key and nothing else. An earlier draft also matched
    /// <c>.WorldId</c>, which produced twelve false positives: <c>CrossWorldPeerConfig.WorldId</c> and
    /// the pre-existing display-oriented <c>WorldIdentity.Id</c> surface are different values that
    /// merely share a name. A fence that flags unrelated members would be disabled or allow-listed into
    /// uselessness within a release. The literal key IS the derivation surface: a consumer can only
    /// re-derive this value by reading it.</para>
    /// </summary>
    private static readonly Regex RawKeyRead = new("\"worldId\"", RegexOptions.Compiled);

    /// <summary>Strips line comments so a doc-comment mentioning the legacy key is not an offence.</summary>
    private static readonly Regex LineComment = new(@"^\s*(///|//).*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void NoProductionCodeReadsWorldIdFromConfigurationDirectly()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: Relative(path), Text: LineComment.Replace(File.ReadAllText(path), string.Empty)))
            .Where(file => !AllowedFiles.Contains(file.Path))
            .Where(file => RawKeyRead.IsMatch(file.Text))
            .Select(file => file.Path)
            .OrderBy(path => path, System.StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "Only the world-identity resolver may derive worldId from configuration; every other "
            + "consumer must take the injected WorldId dependency (#2834 AC5). Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// Guards the fence against becoming vacuous: the allow-list must name files that actually exist
    /// and actually mention the key, otherwise a rename would silently reduce this to a scan that can
    /// never fail for the right reason.
    /// </summary>
    [Fact]
    public void AllowedFiles_ExistAndContainTheKey()
    {
        foreach (var relative in AllowedFiles)
        {
            var absolute = Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(absolute).ShouldBeTrue($"Allow-listed file '{relative}' does not exist.");
            RawKeyRead.IsMatch(LineComment.Replace(File.ReadAllText(absolute), string.Empty))
                .ShouldBeTrue($"Allow-listed file '{relative}' no longer references worldId - remove it from the allow-list.");
        }
    }


    private string Relative(string absolute) =>
        Path.GetRelativePath(Repository.Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

}
